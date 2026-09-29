using MySql.Data.MySqlClient;
using System.Data;
using System.Globalization;

namespace Core.MTCalSync
{
	// Plain ADO.NET data access that opens a fresh connection per call. Session state
	// doesn't survive between calls, so GET_LOCK() can't be used; SyncLock uses a lease
	// row in `sync_lock` instead.
	public class DataAccess
	{
		string dbConnString = Settings.MySqlDatabaseConnection;

		public string errorMessage { get; set; } = string.Empty;

		public DataSet execQuery(string inSqlString, string inDataSetName, string inSourceTableName)
		{
			DataSet outDS = new DataSet(inDataSetName);
			string tmpString = inSqlString.Trim().ToLower();
			if (tmpString.StartsWith("delete") || tmpString.StartsWith("update") || tmpString.StartsWith("insert"))
				return outDS;

			using var myConn = getCXN();
			using var oDA = new MySqlDataAdapter();
			using var myCmd = new MySqlCommand(inSqlString, myConn);
			oDA.SelectCommand = myCmd;
			try
			{
				oDA.Fill(outDS, inSourceTableName);
			}
			catch (Exception ex)
			{
				errorMessage = ex.Message;
				Common.writeToLog("ERROR in execQuery(): " + Environment.NewLine + inSqlString + Environment.NewLine, ex);
			}
			return outDS;
		}

		public DataSet execQuery(string inSqlString, string inDataSetName, string inSourceTableName, Dictionary<string, object> parameters)
		{
			DataSet outDS = new DataSet(inDataSetName);
			using var myConn = getCXN();
			using var myCmd = new MySqlCommand(inSqlString, myConn);
			if (parameters != null)
				foreach (var param in parameters)
					myCmd.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);

			using var oDA = new MySqlDataAdapter(myCmd);
			try
			{
				oDA.Fill(outDS, inSourceTableName);
			}
			catch (Exception ex)
			{
				errorMessage = ex.Message;
				Common.writeToLog("ERROR in execQuery(parameterized): " + Environment.NewLine + inSqlString + Environment.NewLine, ex);
			}
			return outDS;
		}

		public object? execScalar(string inSqlString, Dictionary<string, object> parameters)
		{
			object? outVal = null;
			using var myConn = getCXN();
			using var myCmd = new MySqlCommand(inSqlString, myConn);
			if (parameters != null)
				foreach (var param in parameters)
					myCmd.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
			try
			{
				outVal = myCmd.ExecuteScalar();
			}
			catch (Exception ex)
			{
				errorMessage = ex.Message;
				Common.writeToLog("ERROR in execScalar(): " + Environment.NewLine + inSqlString + Environment.NewLine, ex);
			}
			return outVal;
		}

		// Runs an INSERT, INSERT IGNORE or upsert and returns LAST_INSERT_ID(). An upsert
		// that updates doesn't set that id, so callers look the row up by its key instead.
		public long insertData(string inInsertString, Dictionary<string, object> parameters)
		{
			long outVal = 0;
			if (string.IsNullOrWhiteSpace(inInsertString)) return outVal;
			var lower = inInsertString.Trim().ToLower();
			if (!lower.StartsWith("insert into") && !lower.StartsWith("insert ignore")) return outVal;

			using var myConn = getCXN();
			using var myCmd = new MySqlCommand(inInsertString.Trim(), myConn);
			if (parameters != null)
				foreach (var param in parameters)
					myCmd.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
			try
			{
				int rows = myCmd.ExecuteNonQuery();
				if (rows > 0)
				{
					myCmd.CommandText = "SELECT LAST_INSERT_ID()";
					myCmd.Parameters.Clear();
					object? idVal = myCmd.ExecuteScalar();
					if (idVal != null && long.TryParse(idVal.ToString(), out long rtn))
						outVal = rtn;
				}
			}
			catch (Exception ex)
			{
				errorMessage = ex.Message;
				Common.writeToLog("ERROR in insertData(parameterized): " + Environment.NewLine + inInsertString + Environment.NewLine, ex);
			}
			return outVal;
		}

		public bool updateData(string inUpdateString, Dictionary<string, object> parameters)
		{
			bool outVal = false;
			using var myConn = getCXN();
			using var myCmd = new MySqlCommand(inUpdateString, myConn);
			if (parameters != null)
				foreach (var param in parameters)
					myCmd.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
			try
			{
				int rtn = myCmd.ExecuteNonQuery();
				outVal = rtn > 0;
			}
			catch (Exception ex)
			{
				errorMessage = ex.Message;
				Common.writeToLog("ERROR in updateData(parameterized): " + Environment.NewLine + inUpdateString + Environment.NewLine, ex);
			}
			return outVal;
		}

		public long deleteData(string inDeleteString, Dictionary<string, object> parameters)
		{
			long outVal = 0;
			if (string.IsNullOrWhiteSpace(inDeleteString) || !inDeleteString.Trim().ToLower().StartsWith("delete from "))
				return outVal;

			using var myConn = getCXN();
			using var myCmd = new MySqlCommand(inDeleteString.Trim(), myConn);
			if (parameters != null)
				foreach (var param in parameters)
					myCmd.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
			try
			{
				outVal = myCmd.ExecuteNonQuery();
			}
			catch (Exception ex)
			{
				errorMessage = ex.Message;
				Common.writeToLog("ERROR in deleteData(parameterized): " + Environment.NewLine + inDeleteString + Environment.NewLine, ex);
			}
			return outVal;
		}

		public MySqlConnection getCXN()
		{
			var tmpCxn = new MySqlConnection(dbConnString);
			try
			{
				tmpCxn.Open();
			}
			catch (Exception ex)
			{
				errorMessage = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
				Common.writeToLog("Error getting db cxn: " + errorMessage);
			}
			return tmpCxn;
		}
	}
}
