using Dapper;
using Hangfire.Annotations;
using Hangfire.Storage;
using System;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Hangfire.SQLite
{
    internal class SQLiteJobQueue : IPersistentJobQueue
    {
        private readonly SQLiteStorage _storage;
        private readonly SQLiteStorageOptions _options;        

        public SQLiteJobQueue([NotNull] SQLiteStorage storage, SQLiteStorageOptions options)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            if (options == null) throw new ArgumentNullException(nameof(options));

            _storage = storage;
            _options = options;
        }

        [NotNull]
        public IFetchedJob Dequeue(string[] queues, CancellationToken cancellationToken)
        {
            if (queues == null) throw new ArgumentNullException(nameof(queues));
            if (queues.Length == 0) throw new ArgumentException("Queue array must be non-empty.", nameof(queues));

            FetchedJob fetchedJob = null;

//            string fetchJobSqlTemplate = string.Format(@"
//delete top (1) from [{0}].JobQueue with (readpast, updlock, rowlock)
//output DELETED.Id, DELETED.JobId, DELETED.Queue
//where (FetchedAt is null or FetchedAt < DATEADD(second, @timeout, GETUTCDATE()))
//and Queue in @queues", _storage.GetSchemaName());

            string fetchNextJobSqlTemplate = 
$@"select * from [{_storage.SchemaName}.JobQueue]
where (FetchedAt is null or FetchedAt < @fetchedAt)
and Queue in @queues
limit 1";

            string dequeueJobSqlTemplate = 
$@"update [{_storage.SchemaName}.JobQueue] set FetchedAt = @fetchedAt where Id = @id";

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                _storage.UseConnection(connection =>
                {
                    fetchedJob = connection.Query<FetchedJob>(
                               fetchNextJobSqlTemplate,
                               new { queues = queues, fetchedAt = DateTime.UtcNow })
                               .SingleOrDefault();

                    if (fetchedJob != null)
                    {
                        // update
                        connection.Execute(dequeueJobSqlTemplate,
                            new { id = fetchedJob.Id, fetchedAt = DateTime.UtcNow });
                    }
                }, true);

                if (fetchedJob == null)
                {
                    cancellationToken.WaitHandle.WaitOne(_options.QueuePollInterval);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            } while (fetchedJob == null);

            return new SQLiteFetchedJob(
                _storage,
                fetchedJob.Id,                
                fetchedJob.JobId.ToString(CultureInfo.InvariantCulture),
                fetchedJob.Queue);
        }

        public void Enqueue(IDbConnection connection, string queue, string jobId)
        {
            string enqueueJobSql = 
$@"insert into [{_storage.SchemaName}.JobQueue] (JobId, Queue) values (@jobId, @queue)";

            connection.Execute(enqueueJobSql, new { jobId = long.Parse(jobId), queue = queue });
        }

        [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
        private class FetchedJob
        {
            public int Id { get; set; }
            public int JobId { get; set; }
            public string Queue { get; set; }
        }
    }
}