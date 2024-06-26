using Dapper;
using Hangfire.Annotations;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.SQLite.Entities;
using Hangfire.Storage;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;

namespace Hangfire.SQLite
{
    internal class SQLiteStorageConnection : JobStorageConnection
    {
        private readonly SQLiteStorage _storage;

        public SQLiteStorageConnection([NotNull] SQLiteStorage storage)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            _storage = storage;
        }

        public override IWriteOnlyTransaction CreateWriteTransaction()
        {
            return new SQLiteWriteOnlyTransaction(_storage);
        }

        public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout)
        {
            return new SQLiteDistributedLock();
        }

        public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken)
        {
            if (queues == null || queues.Length == 0) throw new ArgumentNullException(nameof(queues));

            var providers = queues
                .Select(queue => _storage.QueueProviders.GetProvider(queue))
                .Distinct()
                .ToArray();

            if (providers.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Multiple provider instances registered for queues: {String.Join(", ", queues)}. You should choose only one type of persistent queues per server instance.");
            }

            var persistentQueue = providers[0].GetJobQueue();
            return persistentQueue.Dequeue(queues, cancellationToken);
        }

        public override string CreateExpiredJob(
            Job job,
            IDictionary<string, string> parameters,
            DateTime createdAt,
            TimeSpan expireIn)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            string createJobSql =
$@"insert into [{_storage.SchemaName}.Job] (InvocationData, Arguments, CreatedAt, ExpireAt)
values (@invocationData, @arguments, @createdAt, @expireAt);
SELECT last_insert_rowid()";

            var invocationData = InvocationData.SerializeJob(job);

            return _storage.UseConnection(connection =>
            {
                var jobId = connection.ExecuteScalar<long>(
                    createJobSql,
                    new
                    {
                        invocationData = SerializationHelper.Serialize(invocationData),
                        arguments = invocationData.Arguments,
                        createdAt = createdAt,
                        expireAt = createdAt.Add(expireIn)
                    }).ToString();

                if (parameters.Count > 0)
                {
                    var parameterArray = new object[parameters.Count];
                    int parameterIndex = 0;
                    foreach (var parameter in parameters)
                    {
                        parameterArray[parameterIndex++] = new
                        {
                            jobId = long.Parse(jobId),
                            name = parameter.Key,
                            value = parameter.Value
                        };
                    }

                    string insertParameterSql =
$@"insert into [{_storage.SchemaName}.JobParameter] (JobId, Name, Value)
values (@jobId, @name, @value)";

                    connection.Execute(insertParameterSql, parameterArray);
                }

                return jobId;
            }, true);
        }

        public override JobData? GetJobData(string id)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));

            string sql =
$@"select InvocationData, StateName, Arguments, CreatedAt from [{_storage.SchemaName}.Job] where Id = @id";

            return _storage.UseConnection(connection =>
            {
                var jobData = connection.Query<SqlJob>(sql, new { id = id })
                    .SingleOrDefault();

                if (jobData == null) return null;

                // TODO: conversion exception could be thrown.
                var invocationData = SerializationHelper.Deserialize < InvocationData >(jobData.InvocationData);
                invocationData.Arguments = jobData.Arguments;

                Job? job = null;
                JobLoadException loadException = null;

                try
                {
                    job = invocationData.DeserializeJob();
                }
                catch (JobLoadException ex)
                {
                    loadException = ex;
                }

                return new JobData
                {
                    Job = job,
                    State = jobData.StateName,
                    CreatedAt = jobData.CreatedAt,
                    LoadException = loadException
                };
            });
        }

        public override StateData? GetStateData(string jobId)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            string sql =
$@"select s.Name, s.Reason, s.Data
from [{_storage.SchemaName}.State] s
inner join [{_storage.SchemaName}.Job] j on j.StateId = s.Id
where j.Id = @jobId";

            return _storage.UseConnection(connection =>
            {
                var sqlState = connection.Query<SqlState>(sql, new { jobId = long.Parse(jobId) }).SingleOrDefault();
                if (sqlState == null)
                {
                    return null;
                }

                var data = new Dictionary<string, string>(
                   SerializationHelper.Deserialize<Dictionary<string, string>>(sqlState.Data),
                    StringComparer.OrdinalIgnoreCase);

                return new StateData
                {
                    Name = sqlState.Name,
                    Reason = sqlState.Reason,
                    Data = data
                };
            });
        }

        public override void SetJobParameter(string id, string name, string value)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (name == null) throw new ArgumentNullException(nameof(name));

            _storage.UseConnection(connection =>
            {
                string tableName = $"[{_storage.SchemaName}.JobParameter]";
                long jobId = long.Parse(id);
                var fetchedParam = connection.Query<JobParameter>($"select * from {tableName} where JobId = @jobId and Name = @name",
                  new { jobId = jobId, name = name }).Any();

                if (!fetchedParam)
                {
                    // insert
                    connection.Execute($@"insert into {tableName} (JobId, Name, Value) values (@jobId, @name, @value);",
                    new { jobId = jobId, name, value });
                }
                else
                {
                    // update
                    connection.Execute($@"update {tableName} set Value = @value where JobId = @jobId and Name = @name;",
                    new { jobId = jobId, name, value });
                }
            }, true);
        }

        public override string GetJobParameter(string id, string name)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (name == null) throw new ArgumentNullException(nameof(name));

            return _storage.UseConnection(connection => connection.ExecuteScalar<string>(
                $@"select Value from [{_storage.SchemaName}.JobParameter] where JobId = @id and Name = @name limit 1",
                new { id = long.Parse(id), name = name }));
        }

        public override HashSet<string> GetAllItemsFromSet(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            return _storage.UseConnection(connection =>
            {
                var result = connection.Query<string>(
                    $@"select Value from [{_storage.SchemaName}.Set] where [Key] = @key",
                    new { key });

                return new HashSet<string>(result);
            });
        }

        public override string GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (toScore < fromScore) throw new ArgumentException("The `toScore` value must be higher or equal to the `fromScore` value.");

            return _storage.UseConnection(connection => connection.ExecuteScalar<string>(
                $@"select Value from [{_storage.SchemaName}.Set] where [Key] = @key and Score between @from and @to order by Score limit 1",
                new { key, from = fromScore, to = toScore }));
        }

        public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (keyValuePairs == null) throw new ArgumentNullException(nameof(keyValuePairs));

            //            const string sql = @"
            //;merge [HangFire.Hash] with (holdlock) as Target
            //using (VALUES (@key, @field, @value)) as Source ([Key], Field, Value)
            //on Target.[Key] = Source.[Key] and Target.Field = Source.Field
            //when matched then update set Value = Source.Value
            //when not matched then insert ([Key], Field, Value) values (Source.[Key], Source.Field, Source.Value);";

            _storage.UseTransaction((connection, transaction) =>
            {
                string tableName = $"[{_storage.SchemaName}.Hash]";
                var selectSqlStr = $"select * from {tableName} where [Key] = @key and Field = @field";
                var insertSqlStr = $"insert into {tableName} ([Key], Field, Value) values (@key, @field, @value)";
                var updateSqlStr = $"update {tableName} set Value = @value where [Key] = @key and Field = @field";
                foreach (var keyValuePair in keyValuePairs)
                {
                    var fetchedHash = connection.Query<SqlHash>(selectSqlStr,
                        new { key = key, field = keyValuePair.Key }, transaction);
                    if (!fetchedHash.Any())
                    {
                        connection.Execute(insertSqlStr,
                            new { key = key, field = keyValuePair.Key, value = keyValuePair.Value },
                            transaction);
                    }
                    else
                    {
                        connection.Execute(updateSqlStr,
                            new { key = key, field = keyValuePair.Key, value = keyValuePair.Value },
                            transaction);
                    }
                }
            });
        }

        public override Dictionary<string, string> GetAllEntriesFromHash(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            return _storage.UseConnection(connection =>
            {
                var result = connection.Query<SqlHash>(
                    $"select Field, Value from [{_storage.SchemaName}.Hash] where [Key] = @key",
                    new { key })
                    .ToDictionary(x => x.Field, x => x.Value);

                return result.Count != 0 ? result : null;
            });
        }

        public override void AnnounceServer(string serverId, ServerContext context)
        {
            if (serverId == null) throw new ArgumentNullException(nameof(serverId));
            if (context == null) throw new ArgumentNullException(nameof(context));

            var data = new ServerData
            {
                WorkerCount = context.WorkerCount,
                Queues = context.Queues,
                StartedAt = DateTime.UtcNow,
            };

            _storage.UseConnection(connection =>
            {
                string tableName = $"[{_storage.SchemaName}.Server]";
                // select by serverId
                var serverResult = connection.Query<Entities.Server>(
                    $"select * from {tableName} where Id = @id",
                    new { id = serverId }).SingleOrDefault();

                if (serverResult == null)
                {
                    // if not found insert
                    connection.Execute($"insert into {tableName} (Id, Data, LastHeartbeat) values (@id, @data, @lastHeartbeat)",
                        new { id = serverId, data = JobHelper.ToJson(data), lastHeartbeat = DateTime.UtcNow });
                }
                else
                {
                    // if found, update data + heartbeart
                    connection.Execute($"update {tableName} set Data = @data, LastHeartbeat = @lastHeartbeat where Id = @id",
                        new { id = serverId, data = JobHelper.ToJson(data), lastHeartbeat = DateTime.UtcNow });
                }
            }, true);

            //_connection.Execute(
            //    @";merge [HangFire.Server] with (holdlock) as Target "
            //    + @"using (VALUES (@id, @data, @heartbeat)) as Source (Id, Data, Heartbeat) "  // << SOURCE
            //    + @"on Target.Id = Source.Id "
            //    + @"when matched then UPDATE set Data = Source.Data, LastHeartbeat = Source.Heartbeat "
            //    + @"when not matched then INSERT (Id, Data, LastHeartbeat) values (Source.Id, Source.Data, Source.Heartbeat);",
            //    new { id = serverId, data = JobHelper.ToJson(data), heartbeat = DateTime.UtcNow });
        }

        public override void RemoveServer(string serverId)
        {
            if (serverId == null) throw new ArgumentNullException(nameof(serverId));

            _storage.UseConnection(connection =>
            {
                connection.Execute(
                    $@"delete from [{_storage.SchemaName}.Server] where Id = @id",
                    new { id = serverId });
            }, true);
        }

        public override void Heartbeat(string serverId)
        {
            if (serverId == null) throw new ArgumentNullException(nameof(serverId));

            _storage.UseConnection(connection =>
            {
                connection.Execute(
                    $@"update [{_storage.SchemaName}.Server] set LastHeartbeat = @lastHeartbeat where Id = @id",
                    new { id = serverId, lastHeartbeat = DateTime.UtcNow });
            }, true);
        }

        public override int RemoveTimedOutServers(TimeSpan timeOut)
        {
            if (timeOut.Duration() != timeOut)
            {
                throw new ArgumentException("The `timeOut` value must be positive.", nameof(timeOut));
            }

            return _storage.UseConnection(connection => connection.Execute(
                $@"delete from [{_storage.SchemaName}.Server] where LastHeartbeat < @timeOutAt",
                new { timeOutAt = DateTime.UtcNow.Add(timeOut.Negate()) }
            ), true);
        }

        public override long GetSetCount(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            return _storage.UseConnection(connection => connection.Query<int>(
                $"select count([Key]) from [{_storage.SchemaName}.Set] where [Key] = @key",
                new { key = key }).First());
        }

        public override List<string> GetRangeFromSet(string key, int startingFrom, int endingAt)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            string query =
$@"select [Value] 
from [{_storage.SchemaName}.Set]
where [Key] = @key 
order by Id asc
limit @limit offset @offset";

            return _storage.UseConnection(connection => connection
                .Query<string>(query, new { key = key, limit = endingAt - startingFrom + 1, offset = startingFrom })
                .ToList());
        }

        public override TimeSpan GetSetTtl(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            string query =
$@"select min([ExpireAt]) from [{_storage.SchemaName}.Set]
where [Key] = @key";

            return _storage.UseConnection(connection =>
            {
                var result = connection.ExecuteScalar<DateTime?>(query, new { key = key });
                if (!result.HasValue) return TimeSpan.FromSeconds(-1);

                return result.Value - DateTime.UtcNow;
            });
        }

        public override long GetCounter(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            string query =
$@"select sum(s.[Value]) from (select sum([Value]) as [Value] from [{_storage.SchemaName}.Counter]
where [Key] = @key
union all
select [Value] from [{_storage.SchemaName}.AggregatedCounter]
where [Key] = @key) as s";

            return _storage.UseConnection(connection =>
                connection.ExecuteScalar<long?>(query, new { key = key }) ?? 0);
        }

        public override long GetHashCount(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            string query =
$@"select count([Id]) from [{_storage.SchemaName}.Hash]
where [Key] = @key";

            return _storage.UseConnection(connection => connection.ExecuteScalar<long>(query, new { key = key }));
        }

        public override TimeSpan GetHashTtl(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            string query =
$@"select min([ExpireAt]) from [{_storage.SchemaName}.Hash]
where [Key] = @key";

            return _storage.UseConnection(connection =>
            {
                var result = connection.ExecuteScalar<DateTime?>(query, new { key = key });
                if (!result.HasValue) return TimeSpan.FromSeconds(-1);

                return result.Value - DateTime.UtcNow;
            });
        }

        public override string GetValueFromHash(string key, string name)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (name == null) throw new ArgumentNullException(nameof(name));

            string query =
$@"select [Value] from [{_storage.SchemaName}.Hash]
where [Key] = @key and [Field] = @field";

            return _storage.UseConnection(connection => connection
                .ExecuteScalar<string>(query, new { key = key, field = name }));
        }

        public override long GetListCount(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            string query =
$@"select count([Id]) from [{_storage.SchemaName}.List]
where [Key] = @key";

            return _storage.UseConnection(connection => connection.ExecuteScalar<long>(query, new { key = key }));
        }

        public override TimeSpan GetListTtl(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            string query =
$@"select min([ExpireAt]) from [{_storage.SchemaName}.List]
where [Key] = @key";

            return _storage.UseConnection(connection =>
            {
                var result = connection.ExecuteScalar<DateTime?>(query, new { key = key });
                if (!result.HasValue) return TimeSpan.FromSeconds(-1);

                return result.Value - DateTime.UtcNow;
            });
        }

        public override List<string> GetRangeFromList(string key, int startingFrom, int endingAt)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            string query =
    $@"select [Value] 
	from [{_storage.SchemaName}.List]
	where [Key] = @key 
	order by Id desc
	limit @limit offset @offset";

            return _storage.UseConnection(connection => connection
                .Query<string>(query, new { key = key, limit = endingAt - startingFrom + 1, offset = startingFrom })
                .ToList());
        }

        public override List<string> GetAllItemsFromList(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            string query =
$@"select [Value] from [{_storage.SchemaName}.List]
where [Key] = @key
order by [Id] desc";

            return _storage.UseConnection(connection => connection
                .Query<string>(query, new { key = key })
                .ToList());
        }
    }
}
