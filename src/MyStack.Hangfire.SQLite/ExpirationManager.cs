using Dapper;
using Hangfire.Logging;
using Hangfire.Server;
using System;
using System.Threading;

namespace Hangfire.SQLite
{
    internal class ExpirationManager : IServerComponent
    {
        private static readonly ILog Logger = LogProvider.For<ExpirationManager>();

        private const string DistributedLockKey = "locks:expirationmanager";
        private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan DelayBetweenPasses = TimeSpan.FromSeconds(1);
        private const int NumberOfRecordsInSinglePass = 1000;

        private static readonly string[] ProcessedTables =
        {
            "AggregatedCounter",
            "Job",
            "List",
            "Set",
            "Hash",
        };

        private readonly SQLiteStorage _storage;
        private readonly TimeSpan _checkInterval;

        public ExpirationManager(SQLiteStorage storage)
            : this(storage, TimeSpan.FromHours(1))
        {
        }

        public ExpirationManager(SQLiteStorage storage, TimeSpan checkInterval)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));

            _storage = storage;
            _checkInterval = checkInterval;
        }

        public void Execute(CancellationToken cancellationToken)
        {
            foreach (var table in ProcessedTables)
            {
                Logger.Debug($"Removing outdated records from the '{table}' table...");

                int removedCount = 0;

                do
                {
                    _storage.UseConnection(connection =>
                    {                        
                        removedCount = connection.Execute(
                               $@"delete from [{ _storage.SchemaName}.{table}] where Id in (
                                    select Id from [{_storage.SchemaName}.{table}]
                                    where ExpireAt < @expireAt
                                    limit @limit)", 
                            new { limit = NumberOfRecordsInSinglePass, expireAt = DateTime.UtcNow });
                                                
                    }, true);

                    if (removedCount > 0)
                    {
                        Logger.Trace($"Removed {removedCount} outdated record(s) from the '{table}' table.");

                        cancellationToken.WaitHandle.WaitOne(DelayBetweenPasses);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                } while (removedCount != 0);
            }

            cancellationToken.WaitHandle.WaitOne(_checkInterval);
        }

        public override string ToString()
        {
            return GetType().ToString();
        }
    }
}
