using Core.MTCalSync;

namespace Worker.MTCalSync
{
	// Thin CLI over Core.MTCalSync — the self-host engine worker. The `sync` subcommand
	// is what the systemd timer runs (mtcalsync-worker@sync). Job logic lives in Core
	// (SyncEngine / CliCommands). This is the self-host worker: sync runs for every
	// enabled pair, with no subscription/entitlement gating layered on top.
	//
	//   dotnet Worker.MT-CalSync.dll setup-check
	//   dotnet Worker.MT-CalSync.dll add-pair --m365-email a@corp.com --google-email b@fam.com
	//   dotnet Worker.MT-CalSync.dll list-pairs
	//   dotnet Worker.MT-CalSync.dll sync [--pair N] [--full] [--dry-run] [--force]
	//   dotnet Worker.MT-CalSync.dll status | history --pair N | resync --pair N
	//   dotnet Worker.MT-CalSync.dll pause --pair N | resume --pair N
	//   dotnet Worker.MT-CalSync.dll dead-letters --pair N [--resolve M | --resolve-all]
	//   dotnet Worker.MT-CalSync.dll test-email | set-secret <name> <value>
	public class Program
	{
		public static async Task<int> Main(string[] args)
		{
			if (args.Length == 0) { Usage(); return 1; }
			string cmd = args[0].ToLowerInvariant();
			var a = new Args(args);
			Common.writeToLog($"Worker.MT-CalSync started — command: {cmd}");

			try
			{
				switch (cmd)
				{
					case "setup-check":
					{
						var sc = new SetupChecker();
						bool ok = sc.Run();
						foreach (var line in sc.Lines) Console.WriteLine(line);
						return ok ? 0 : 1;
					}

					case "add-pair":
						CliCommands.AddPair(a.Str("name"), a.Str("m365-email"), a.Str("google-email"),
							a.Str("m365-cal"), a.Str("google-cal"), a.Str("direction"), a.Str("fidelity"), a.Str("recurrence"));
						return 0;

					case "list-pairs":
						CliCommands.ListPairs(); return 0;

					case "sync":
					{
						long pair = a.Long("pair");
						bool dry = a.Flag("dry-run");
						bool force = a.Flag("force");
						bool full = a.Flag("full");
						if (pair > 0)
						{
							var run = await SyncEngine.RunPairById(pair, "manual", dry, force, full);
							return run != null && (run.status == "failed" || run.status == "aborted_circuit_breaker") ? 2 : 0;
						}
						// Default tick = the scheduler (due pairs, concurrency, backoff, account
						// gates); --sequential keeps the simple run-everything loop for debugging.
						if (a.Flag("sequential"))
							return await SyncEngine.RunAllEnabled("timer", dry, force, full);
						return await SyncScheduler.RunDue("timer", dry, force, full);
					}

					case "status":
						CliCommands.Status(); return 0;

					case "history":
						CliCommands.History(a.Long("pair"), (int)Math.Max(1, a.Long("limit", 20))); return 0;

					case "inspect":
						await CliCommands.Inspect(RequirePair(a), a.Str("subject")); return 0;

					case "purge-mirror":
						await CliCommands.PurgeMirror(RequirePair(a), a.Str("provider"), a.Str("id")); return 0;

					case "goid":   // debug: goid decode <hex> | goid encode <cleanUid>
						if (args.Length < 3) { Console.WriteLine("Usage: goid <decode|encode> <value>"); return 1; }
						Console.WriteLine(args[1].ToLowerInvariant() == "encode"
							? GlobalObjectId.EncodeUid(args[2])
							: (GlobalObjectId.TryExtractUid(args[2]) ?? "(not a vCal-Uid GlobalObjectId)"));
						return 0;

					case "resync":
						CliCommands.Resync(RequirePair(a)); return 0;

					case "pause":
						CliCommands.Pause(RequirePair(a), true); return 0;

					case "resume":
						CliCommands.Pause(RequirePair(a), false); return 0;

					case "remove-pair":
					{
						var res = await CliCommands.TeardownPair(RequirePair(a));
						Console.WriteLine(res.Message);
						return res.Removed ? 0 : (res.Busy ? 1 : 2);
					}

					case "sweep-strays":
						await CliCommands.SweepStrays(RequirePair(a), a.Long("dead")); return 0;

					case "repair-chains":
						await CliCommands.RepairChains(RequirePair(a), a.Flag("apply")); return 0;

					case "dead-letters":
						CliCommands.DeadLetters(a.Long("pair"), a.Long("resolve"), a.Flag("resolve-all")); return 0;

					case "test-email":
						CliCommands.TestEmail(); return 0;

					case "set-secret":
						if (args.Length < 3) { Console.WriteLine("Usage: set-secret <name> <value>"); return 1; }
						CliCommands.SetSecret(args[1], args[2]); return 0;

					case "set-admin-password":
						CliCommands.SetAdminPassword(a.Str("password")); return 0;

					case "migrate-secrets":
						CliCommands.MigrateSecrets(); return 0;

					default:
						Console.WriteLine($"Unknown command: {cmd}"); Usage(); return 1;
				}
			}
			catch (Exception ex)
			{
				Common.writeToLog($"FATAL in Worker.MT-CalSync [{cmd}]:", ex);
				Console.Error.WriteLine($"FATAL: {ex.Message}");
				return 2;
			}
		}

		private static long RequirePair(Args a)
		{
			long p = a.Long("pair");
			if (p <= 0) throw new ArgumentException("This command requires --pair <id>.");
			return p;
		}

		private static void Usage()
		{
			Console.WriteLine("Usage: Worker.MT-CalSync <command> [--flags]");
			Console.WriteLine("  setup-check                              validate creds, DB, calendars, SMTP");
			Console.WriteLine("  add-pair --m365-email X --google-email Y [--name N] [--m365-cal C] [--google-cal C] [--direction bidirectional] [--fidelity full_detail] [--recurrence instance|series]");
			Console.WriteLine("  list-pairs | status | history --pair N [--limit K]");
			Console.WriteLine("  inspect --pair N [--subject TEXT]        read-only: dump both sides' in-window events + provenance");
			Console.WriteLine("  purge-mirror --pair N --provider P --id X delete a managed mirror event + tombstone its mapping (guarded)");
			Console.WriteLine("  sync [--pair N] [--full] [--dry-run] [--force]");
			Console.WriteLine("  resync --pair N | pause --pair N | resume --pair N");
			Console.WriteLine("  remove-pair --pair N                     delete a pair + the events it mirrored onto M365");
			Console.WriteLine("  sweep-strays --pair N --dead M           delete stamped mirrors of DEAD pair M from live pair N's calendars");
			Console.WriteLine("  repair-chains --pair N [--apply]         find/dismantle mirror-of-mirror chains (origin is itself a stamped mirror)");
			Console.WriteLine("  dead-letters --pair N [--resolve M | --resolve-all]");
			Console.WriteLine("  test-email | set-secret <name> <value>");
			Console.WriteLine("  set-admin-password --password P          set the self-host portal operator password");
			Console.WriteLine("  migrate-secrets                          re-encrypt legacy stored secrets under DataEncryptionKey");
		}
	}

	// Minimal flag parser: `--key value` pairs and boolean `--flag`.
	internal class Args
	{
		private readonly Dictionary<string, string> _kv = new(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

		public Args(string[] args)
		{
			for (int i = 1; i < args.Length; i++)
			{
				if (!args[i].StartsWith("--")) continue;
				string key = args[i].Substring(2);
				if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { _kv[key] = args[++i]; }
				else _flags.Add(key);
			}
		}

		public string Str(string key, string def = "") => _kv.TryGetValue(key, out var v) ? v : def;
		public long Long(string key, long def = 0) => _kv.TryGetValue(key, out var v) && long.TryParse(v, out var l) ? l : def;
		public bool Flag(string key) => _flags.Contains(key) || _kv.ContainsKey(key);
	}
}
