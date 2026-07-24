namespace Core.MTCalSync
{
	// Shared base for DAL entities.
	public class @base
	{
		public string errorMessage { get; set; } = string.Empty;
		public long workingUserID { get; set; }
	}
}
