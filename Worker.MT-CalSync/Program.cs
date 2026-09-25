using System.Diagnostics;
using System.Text;
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
	//   dotnet Worker.MT-CalSync.dll test-email | set-secret <name> | set-admin-password
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
							if (run == null) return 1;   // no such pair (RunPairById has said so)
							return run.status == "failed" || run.status == "aborted_circuit_breaker" ? 2 : 0;
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
						return CliCommands.Pause(RequirePair(a), true) ? 0 : 1;

					case "resume":
						return CliCommands.Pause(RequirePair(a), false) ? 0 : 1;

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
						return CliCommands.TestEmail() ? 0 : 1;

					// Secrets are read from a prompt (or stdin) rather than taken as arguments:
					// the documented `mtcs` alias runs through sudo, which records the whole
					// command line in the system log. A value on the command line still works,
					// for compatibility, but the docs no longer show it.
					case "set-secret":
					{
						if (args.Length < 2 || args[1].StartsWith("--")) { Console.WriteLine("Usage: set-secret <name>   (you'll be prompted for the value)"); return 1; }
						string value = args.Length >= 3 ? args[2] : ReadSecret($"Value for {args[1]}: ");
						return CliCommands.SetSecret(args[1], value) ? 0 : 1;
					}

					case "set-admin-password":
					{
						string password = a.Str("password");
						if (string.IsNullOrEmpty(password)) password = ReadSecret("New operator password: ", confirm: true);
						return CliCommands.SetAdminPassword(password) ? 0 : 1;
					}

					case "migrate-secrets":
						return CliCommands.MigrateSecrets() ? 0 : 1;

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

		// Piped input is read as one line, so a script can run
		//   printf '%s\n' "$PW" | mtcs set-admin-password
		// At a terminal the value is typed without echo (twice when confirm is set).
		private static string ReadSecret(string prompt, bool confirm = false)
		{
			if (Console.IsInputRedirected) return (Console.In.ReadLine() ?? string.Empty).TrimEnd('\r');
			string first = ReadHidden(prompt);
			if (!confirm || first.Length == 0) return first;
			if (ReadHidden("Again, to confirm: ") == first) return first;
			Console.WriteLine("The two entries didn't match.");
			return string.Empty;
		}

		// Echo goes off before the prompt appears (stty) and the line is read straight from
		// /dev/tty, the way sudo reads its own password. Console.ReadKey only turns echo off
		// while it is waiting for a key, so input that arrived just ahead of the first call
		// (a paste, or a script answering the prompt the instant it showed) was echoed.
		private static string ReadHidden(string prompt)
		{
			if (!OperatingSystem.IsWindows() && SetTerminalEcho(false))
			{
				ConsoleCancelEventHandler restoreOnCtrlC = (_, _) => SetTerminalEcho(true);
				Console.CancelKeyPress += restoreOnCtrlC;
				try
				{
					Console.Error.Write(prompt);
					using var tty = new FileStream("/dev/tty", FileMode.Open, FileAccess.Read);
					var bytes = new List<byte>();
					for (int b = tty.ReadByte(); b != -1 && b != '\n'; b = tty.ReadByte()) bytes.Add((byte)b);
					return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
				}
				catch (IOException) { }   // no usable /dev/tty: fall through to per-key reads
				finally
				{
					SetTerminalEcho(true);
					Console.CancelKeyPress -= restoreOnCtrlC;
					Console.Error.WriteLine();
				}
			}

			Console.Error.Write(prompt);
			var sb = new StringBuilder();
			while (true)
			{
				var key = Console.ReadKey(intercept: true);
				if (key.Key == ConsoleKey.Enter) break;
				if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
				if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
			}
			Console.Error.WriteLine();
			return sb.ToString();
		}

		// stty works on its standard input, which it inherits from us: the terminal.
		private static bool SetTerminalEcho(bool on)
		{
			try
			{
				using var p = Process.Start(new ProcessStartInfo("stty", on ? "echo" : "-echo") { UseShellExecute = false });
				if (p == null) return false;
				p.WaitForExit(5000);
				return p.HasExited && p.ExitCode == 0;
			}
			catch { return false; }
		}

		private static void Usage()
		{
			Console.WriteLine("Usage: Worker.MT-CalSync <command> [--flags]");
			Console.WriteLine("  setup-check                              check the DB and settings, and live-read each paired calendar");
			Console.WriteLine("  add-pair --m365-email X --google-email Y [--name N] [--m365-cal C] [--google-cal C] [--direction bidirectional] [--fidelity full_detail] [--recurrence instance|series]");
			Console.WriteLine("                                           creates an app-credential pair, paused");
			Console.WriteLine("  list-pairs");
			Console.WriteLine("  status");
			Console.WriteLine("  history --pair N [--limit K]");
			Console.WriteLine("  inspect --pair N [--subject TEXT]        read-only: dump both sides' in-window events + provenance");
			Console.WriteLine("  purge-mirror --pair N --provider P --id X delete a managed mirror event + tombstone its mapping (guarded)");
			Console.WriteLine("  sync [--pair N] [--full] [--dry-run] [--force]");
			Console.WriteLine("  resync --pair N | pause --pair N | resume --pair N");
			Console.WriteLine("  remove-pair --pair N                     delete a pair and the events it mirrored, on both calendars");
			Console.WriteLine("  sweep-strays --pair N --dead M           delete stamped mirrors of DEAD pair M from live pair N's calendars");
			Console.WriteLine("  repair-chains --pair N [--apply]         find/dismantle mirror-of-mirror chains (origin is itself a stamped mirror)");
			Console.WriteLine("  dead-letters --pair N [--resolve M | --resolve-all]");
			Console.WriteLine("  test-email                               send a test alert through the SMTP settings");
			Console.WriteLine("  set-secret <name>                        store a secret, encrypted (prompts for the value)");
			Console.WriteLine("  set-admin-password                       set the self-host portal operator password (prompts)");
			Console.WriteLine("  migrate-secrets                          check that every stored secret is in the current (v2) format");
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
