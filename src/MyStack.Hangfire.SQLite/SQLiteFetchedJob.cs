using Dapper;
using Hangfire.Annotations;
using Hangfire.Storage;
using System;

namespace Hangfire.SQLite
{
    internal class SQLiteFetchedJob : IFetchedJob
    {
        private readonly SQLiteStorage _storage;       

        public SQLiteFetchedJob(
            [NotNull] SQLiteStorage storage,
            int id,           
            string jobId,
            string queue)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));            
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));
            if (queue == null) throw new ArgumentNullException(nameof(queue));

            _storage = storage;

            Id = id;
            JobId = jobId;
            Queue = queue;
        }

        public int Id { get; private set; }
        public string JobId { get; private set; }
        public string Queue { get; private set; }

        public void RemoveFromQueue()
        {
            _storage.UseConnection(connection =>
            {
                connection.Execute($@"delete from [{_storage.SchemaName}.JobQueue] where Id = @id",
                    new { id = Id });
            }, true);
        }

        public void Requeue()
        {
            _storage.UseConnection(connection =>
            {
                connection.Execute($@"update [{_storage.SchemaName}.JobQueue] set FetchedAt = null where Id = @id",
                    new { id = Id });
            }, true);
        }

        public void Dispose()
        {
            
        }
    }
}
