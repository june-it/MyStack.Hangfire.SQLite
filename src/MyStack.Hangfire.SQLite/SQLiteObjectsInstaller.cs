using Dapper;
using Hangfire.Logging;
using System;
using System.Data.Common;
using System.IO;
using System.Reflection;

namespace Hangfire.SQLite
{
    internal static class SQLiteObjectsInstaller
    {
        private const int RequiredSchemaVersion = 5;
        private const int RetryAttempts = 3;        

        private static readonly ILog Log = LogProvider.GetLogger(typeof(SQLiteStorage));

        public static void Install(DbConnection connection)
        {
            Install(connection, null);
        }

        public static void Install(DbConnection connection, string? schema)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            
            Log.Info("Start installing Hangfire SQL objects...");

            var script = GetStringResource(
                typeof(SQLiteObjectsInstaller).GetTypeInfo().Assembly,
                "MyStack.Hangfire.SQLite.Install.sql");

            script = script.Replace("SET @TARGET_SCHEMA_VERSION = 5;", "SET @TARGET_SCHEMA_VERSION = " + RequiredSchemaVersion + ";");

            script = script.Replace("$(HangFireSchema)", !string.IsNullOrWhiteSpace(schema) ? schema : Constants.DefaultSchema);

            for (var i = 0; i < RetryAttempts; i++)
            {
                try
                {
                    connection.Execute(script);
                    break;
                }
                catch
                {
                    throw;
                }
            }

            Log.Info("Hangfire SQL objects installed.");
        }

        private static string GetStringResource(Assembly assembly, string resourceName)
        {
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) 
                {
                    throw new InvalidOperationException(
                        $"Requested resource `{resourceName}` was not found in the assembly `{assembly}`.");
                }

                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }
}
