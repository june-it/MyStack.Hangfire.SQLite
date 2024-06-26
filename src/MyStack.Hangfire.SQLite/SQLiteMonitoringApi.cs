﻿using Dapper;
using Hangfire.Annotations;
using Hangfire.Common;
using Hangfire.SQLite.Entities;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

namespace Hangfire.SQLite
{
    internal class SQLiteMonitoringApi : IMonitoringApi
    {
        private readonly SQLiteStorage _storage;
        private readonly int? _jobListLimit;

        public SQLiteMonitoringApi([NotNull] SQLiteStorage storage, int? jobListLimit)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));

            _storage = storage;
            _jobListLimit = jobListLimit;
        }

        public long ScheduledCount()
        {
            return UseConnection(connection => 
                GetNumberOfJobsByStateName(connection, ScheduledState.StateName));
        }

        public long EnqueuedCount(string queue)
        {
            var queueApi = GetQueueApi(queue);
            var counters = queueApi.GetEnqueuedAndFetchedCount(queue);

            return counters.EnqueuedCount ?? 0;
        }

        public long FetchedCount(string queue)
        {
            var queueApi = GetQueueApi(queue);
            var counters = queueApi.GetEnqueuedAndFetchedCount(queue);

            return counters.FetchedCount ?? 0;
        }

        public long FailedCount()
        {
            return UseConnection(connection =>
                GetNumberOfJobsByStateName(connection, FailedState.StateName));
        }

        public long ProcessingCount()
        {
            return UseConnection(connection =>
                GetNumberOfJobsByStateName(connection, ProcessingState.StateName));
        }

        public JobList<ProcessingJobDto> ProcessingJobs(int @from, int count)
        {
            return UseConnection(connection => GetJobs(
                connection,
                from, count,
                ProcessingState.StateName,
                (sqlJob, job, stateData) => new ProcessingJobDto
                {
                    Job = job,
                    ServerId = stateData.ContainsKey("ServerId") ? stateData["ServerId"] : stateData["ServerName"],
                    StartedAt = JobHelper.DeserializeDateTime(stateData["StartedAt"]),
                }));
        }

        public JobList<ScheduledJobDto> ScheduledJobs(int @from, int count)
        {
            return UseConnection(connection => GetJobs(
                connection,
                from, count,
                ScheduledState.StateName,
                (sqlJob, job, stateData) => new ScheduledJobDto
                {
                    Job = job,
                    EnqueueAt = JobHelper.DeserializeDateTime(stateData["EnqueueAt"]),
                    ScheduledAt = JobHelper.DeserializeDateTime(stateData["ScheduledAt"])
                }));
        }

        public IDictionary<DateTime, long> SucceededByDatesCount()
        {
            return UseConnection(connection => 
                GetTimelineStats(connection, "succeeded"));
        }

        public IDictionary<DateTime, long> FailedByDatesCount()
        {
            return UseConnection(connection => 
                GetTimelineStats(connection, "failed"));
        }

        public IList<ServerDto> Servers()
        {
            return UseConnection<IList<ServerDto>>(connection =>
            {
                var servers = connection.Query<Entities.Server>(
                    $@"select * from [{_storage.SchemaName}.Server]")
                    .ToList();

                var result = new List<ServerDto>();

                foreach (var server in servers)
                {
                    var data = JobHelper.FromJson<ServerData>(server.Data);
                    result.Add(new ServerDto
                    {
                        Name = server.Id,
                        Heartbeat = server.LastHeartbeat,
                        Queues = data.Queues,
                        StartedAt = data.StartedAt ?? DateTime.MinValue,
                        WorkersCount = data.WorkerCount
                    });
                }

                return result;
            });
        }

        public JobList<FailedJobDto> FailedJobs(int @from, int count)
        {
            return UseConnection(connection => GetJobs(
                connection,
                from,
                count,
                FailedState.StateName,
                (sqlJob, job, stateData) => new FailedJobDto
                {
                    Job = job,
                    Reason = sqlJob.StateReason,
                    ExceptionDetails = stateData["ExceptionDetails"],
                    ExceptionMessage = stateData["ExceptionMessage"],
                    ExceptionType = stateData["ExceptionType"],
                    FailedAt = JobHelper.DeserializeNullableDateTime(stateData["FailedAt"])
                }));
        }

        public JobList<SucceededJobDto> SucceededJobs(int @from, int count)
        {
            return UseConnection(connection => GetJobs(
                connection,
                from,
                count,
                SucceededState.StateName,
                (sqlJob, job, stateData) => new SucceededJobDto
                {
                    Job = job,
                    Result = stateData.ContainsKey("Result") ? stateData["Result"] : null,
                    TotalDuration = stateData.ContainsKey("PerformanceDuration") && stateData.ContainsKey("Latency")
                        ? (long?)long.Parse(stateData["PerformanceDuration"]) + (long?)long.Parse(stateData["Latency"])
                        : null,
                    SucceededAt = JobHelper.DeserializeNullableDateTime(stateData["SucceededAt"])
                }));
        }

        public JobList<DeletedJobDto> DeletedJobs(int @from, int count)
        {
            return UseConnection(connection => GetJobs(
                connection,
                from,
                count,
                DeletedState.StateName,
                (sqlJob, job, stateData) => new DeletedJobDto
                {
                    Job = job,
                    DeletedAt = JobHelper.DeserializeNullableDateTime(stateData.ContainsKey("DeletedAt") ? stateData["DeletedAt"] : null)
                }));
        }

        public IList<QueueWithTopEnqueuedJobsDto> Queues()
        {
            var tuples = _storage.QueueProviders
                .Select(x => x.GetJobQueueMonitoringApi())
                .SelectMany(x => x.GetQueues(), (monitoring, queue) => new { Monitoring = monitoring, Queue = queue })
                .OrderBy(x => x.Queue)
                .ToArray();

            var result = new List<QueueWithTopEnqueuedJobsDto>(tuples.Length);

            foreach (var tuple in tuples)
            {
                var enqueuedJobIds = tuple.Monitoring.GetEnqueuedJobIds(tuple.Queue, 0, 5);
                var counters = tuple.Monitoring.GetEnqueuedAndFetchedCount(tuple.Queue);

                var firstJobs = UseConnection(connection => EnqueuedJobs(connection, enqueuedJobIds));

                result.Add(new QueueWithTopEnqueuedJobsDto
                {
                    Name = tuple.Queue,
                    Length = counters.EnqueuedCount ?? 0,
                    Fetched = counters.FetchedCount,
                    FirstJobs = firstJobs
                });
            }

            return result;
        }

        public JobList<EnqueuedJobDto> EnqueuedJobs(string queue, int @from, int perPage)
        {
            var queueApi = GetQueueApi(queue);
            var enqueuedJobIds = queueApi.GetEnqueuedJobIds(queue, from, perPage);

            return UseConnection(connection => EnqueuedJobs(connection, enqueuedJobIds));
        }

        public JobList<FetchedJobDto> FetchedJobs(string queue, int @from, int perPage)
        {
            var queueApi = GetQueueApi(queue);
            var fetchedJobIds = queueApi.GetFetchedJobIds(queue, from, perPage);

            return UseConnection(connection => FetchedJobs(connection, fetchedJobIds));
        }

        public IDictionary<DateTime, long> HourlySucceededJobs()
        {
            return UseConnection(connection => 
                GetHourlyTimelineStats(connection, "succeeded"));
        }

        public IDictionary<DateTime, long> HourlyFailedJobs()
        {
            return UseConnection(connection => 
                GetHourlyTimelineStats(connection, "failed"));
        }

        public JobDetailsDto JobDetails(string jobId)
        {
            return UseConnection(connection =>
            {

                string sql = $@"
select * from [{_storage.SchemaName}.Job] where Id = @id;
select * from [{_storage.SchemaName}.JobParameter] where JobId = @id;
select * from [{_storage.SchemaName}.State] where JobId = @id order by Id desc;";

                using (var multi = connection.QueryMultiple(sql, new { id = jobId }))
                {
                    var job = multi.Read<SqlJob>().SingleOrDefault();
                    if (job == null) return null;

                    var parameters = multi.Read<JobParameter>().ToDictionary(x => x.Name, x => x.Value);
                    var history =
                        multi.Read<SqlState>()
                            .ToList()
                            .Select(x => new StateHistoryDto
                            {
                                StateName = x.Name,
                                CreatedAt = x.CreatedAt,
                                Reason = x.Reason,
                                Data = new Dictionary<string, string>(
                                    JobHelper.FromJson<Dictionary<string, string>>(x.Data),
                                    StringComparer.OrdinalIgnoreCase),
                            })
                            .ToList();

                    return new JobDetailsDto
                    {
                        CreatedAt = job.CreatedAt,
                        ExpireAt = job.ExpireAt,
                        Job = DeserializeJob(job.InvocationData, job.Arguments),
                        History = history,
                        Properties = parameters
                    };
                }
            });
        }

        public long SucceededListCount()
        {
            return UseConnection(connection => 
                GetNumberOfJobsByStateName(connection, SucceededState.StateName));
        }

        public long DeletedListCount()
        {
            return UseConnection(connection => 
                GetNumberOfJobsByStateName(connection, DeletedState.StateName));
        }

        public StatisticsDto GetStatistics()
        {
            string sql = string.Format(@"
select count(Id) from [{0}.Job] where StateName = 'Enqueued';
select count(Id) from [{0}.Job] where StateName = 'Failed';
select count(Id) from [{0}.Job] where StateName = 'Processing';
select count(Id) from [{0}.Job] where StateName = 'Scheduled';
select count(Id) from [{0}.Server];
select sum(s.[Value]) from (
    select sum([Value]) as [Value] from [{0}.Counter] where [Key] = 'stats:succeeded'
    union all
    select [Value] from [{0}.AggregatedCounter] where [Key] = 'stats:succeeded'
) as s;
select sum(s.[Value]) from (
    select sum([Value]) as [Value] from [{0}.Counter] where [Key] = 'stats:deleted'
    union all
    select [Value] from [{0}.AggregatedCounter] where [Key] = 'stats:deleted'
) as s;
select count(*) from [{0}.Set] where [Key] = 'recurring-jobs';
", _storage.SchemaName);

            var statistics = UseConnection(connection =>
            {
                var stats = new StatisticsDto();

                using (var multi = connection.QueryMultiple(sql))
                {
                    stats.Enqueued = multi.ReadSingle<int>();
                    stats.Failed = multi.ReadSingle<int>();
                    stats.Processing = multi.ReadSingle<int>();
                    stats.Scheduled = multi.ReadSingle<int>();

                    stats.Servers = multi.ReadSingle<int>();

                    stats.Succeeded = multi.ReadSingleOrDefault<long?>() ?? 0;
                    stats.Deleted = multi.ReadSingleOrDefault<long?>() ?? 0;

                    stats.Recurring = multi.ReadSingle<int>();
                }
                return stats;
            });

            statistics.Queues = _storage.QueueProviders
                .SelectMany(x => x.GetJobQueueMonitoringApi().GetQueues())
                .Count();

            return statistics;
        }

        private Dictionary<DateTime, long> GetHourlyTimelineStats(
            DbConnection connection, string type)
        {
            var endDate = DateTime.UtcNow;
            var dates = new List<DateTime>();
            for (var i = 0; i < 24; i++)
            {
                dates.Add(endDate);
                endDate = endDate.AddHours(-1);
            }

            var keyMaps = dates.ToDictionary(x => $"stats:{type}:{x.ToString("yyyy-MM-dd-HH")}", x => x);

            return GetTimelineStats(connection, keyMaps);
        }

        private Dictionary<DateTime, long> GetTimelineStats(
            DbConnection connection, string type)
        {
            var endDate = DateTime.UtcNow.Date;
            var dates = new List<DateTime>();
            for (var i = 0; i < 7; i++)
            {
                dates.Add(endDate);
                endDate = endDate.AddDays(-1);
            }

            var keyMaps = dates.ToDictionary(x => $"stats:{type}:{x.ToString("yyyy-MM-dd")}", x => x);

            return GetTimelineStats(connection, keyMaps);
        }

        private Dictionary<DateTime, long> GetTimelineStats(DbConnection connection,
            IDictionary<string, DateTime> keyMaps)
        {
            string sqlQuery = 
$@"select [Key], [Value] as [Count] from [{_storage.SchemaName}.AggregatedCounter]
where [Key] in @keys";

            var valuesMap = connection.Query(
                sqlQuery,
                new { keys = keyMaps.Keys })
                .ToDictionary(x => (string)x.Key, x => (long)x.Count);

            foreach (var key in keyMaps.Keys)
            {
                if (!valuesMap.ContainsKey(key)) valuesMap.Add(key, 0);
            }

            var result = new Dictionary<DateTime, long>();
            for (var i = 0; i < keyMaps.Count; i++)
            {
                var value = valuesMap[keyMaps.ElementAt(i).Key];
                result.Add(keyMaps.ElementAt(i).Value, value);
            }

            return result;
        }

        private IPersistentJobQueueMonitoringApi GetQueueApi(string queueName)
        {
            var provider = _storage.QueueProviders.GetProvider(queueName);
            var monitoringApi = provider.GetJobQueueMonitoringApi();

            return monitoringApi;
        }

        private T UseConnection<T>(Func<DbConnection, T> action, bool isWriteLock = false)
        {
            return _storage.UseConnection(action, isWriteLock);
        }

        private JobList<EnqueuedJobDto> EnqueuedJobs(
            DbConnection connection, IEnumerable<int> jobIds)
        {
            string enqueuedJobsSql = 
$@"select j.*, s.Reason as StateReason, s.Data as StateData 
from [{_storage.SchemaName}.Job] j
left join [{_storage.SchemaName}.State] s on s.Id = j.StateId
where j.Id in @jobIds";

            var jobs = connection.Query<SqlJob>(
                enqueuedJobsSql,
                new { jobIds = jobIds })
                .ToDictionary(x => x.Id, x => x);

            var sortedSqlJobs = jobIds
                .Select(jobId => jobs.ContainsKey(jobId) ? jobs[jobId] : new SqlJob { Id = jobId })
                .ToList();

            return DeserializeJobs(
                sortedSqlJobs,
                (sqlJob, job, stateData) => new EnqueuedJobDto
                {
                    Job = job,
                    State = sqlJob.StateName,
                    EnqueuedAt = sqlJob.StateName == EnqueuedState.StateName
                        ? JobHelper.DeserializeNullableDateTime(stateData["EnqueuedAt"])
                        : null
                });
        }

        private long GetNumberOfJobsByStateName(DbConnection connection, string stateName)
        {
            var sqlQuery = _jobListLimit.HasValue
                ? $@"select count(j.Id) from (select Id from [{_storage.SchemaName}.Job] where StateName = @state limit @limit) as j"
                : $@"select count(Id) from [{_storage.SchemaName}.Job] where StateName = @state";

            var count = connection.ExecuteScalar<int>(
                 sqlQuery,
                 new { state = stateName, limit = _jobListLimit });

            return count;
        }

        private static Job DeserializeJob(string invocationData, string arguments)
        {
            var data = JobHelper.FromJson<InvocationData>(invocationData);
            data.Arguments = arguments;

            try
            {
                return data.Deserialize();
            }
            catch (JobLoadException)
            {
                return null;
            }
        }

        private JobList<TDto> GetJobs<TDto>(
            DbConnection connection,
            int from,
            int count,
            string stateName,
            Func<SqlJob, Job, Dictionary<string, string>, TDto> selector)
        {
            string jobsSql = 
$@"select j.Id, j.InvocationData, j.Arguments, s.Reason as StateReason, s.Data as StateData
  from [{_storage.SchemaName}.Job] j
  left join [{_storage.SchemaName}.State] s on j.StateId = s.Id
  where j.StateName = @stateName
  order by j.Id desc
  limit @limit offset @offset";

            var jobs = connection.Query<SqlJob>(
                        jobsSql,
                        new { stateName = stateName, limit = count, offset = from })
                        .ToList();

            return DeserializeJobs(jobs, selector);
        }

        private static JobList<TDto> DeserializeJobs<TDto>(
            ICollection<SqlJob> jobs,
            Func<SqlJob, Job, Dictionary<string, string>, TDto> selector)
        {
            var result = new List<KeyValuePair<string, TDto>>(jobs.Count);

            foreach (var job in jobs)
            {
                var dto = default(TDto);

                if (job.InvocationData != null)
                {
                    var deserializedData = JobHelper.FromJson<Dictionary<string, string>>(job.StateData);
                    var stateData = deserializedData != null
                        ? new Dictionary<string, string>(deserializedData, StringComparer.OrdinalIgnoreCase)
                        : null;

                    dto = selector(job, DeserializeJob(job.InvocationData, job.Arguments), stateData);
                }

                result.Add(new KeyValuePair<string, TDto>(
                    job.Id.ToString(), dto));
            }

            return new JobList<TDto>(result);
        }

        private JobList<FetchedJobDto> FetchedJobs(
            DbConnection connection, IEnumerable<int> jobIds)
        {
            string fetchedJobsSql = 
$@"select j.*, s.Reason as StateReason, s.Data as StateData 
from [{_storage.SchemaName}.Job] j
left join [{_storage.SchemaName}.State] s on s.Id = j.StateId
where j.Id in @jobIds";

            var jobs = connection.Query<SqlJob>(
                fetchedJobsSql,
                new { jobIds = jobIds })
                .ToList();

            var result = new List<KeyValuePair<string, FetchedJobDto>>(jobs.Count);

            foreach (var job in jobs)
            {
                result.Add(new KeyValuePair<string, FetchedJobDto>(
                    job.Id.ToString(),
                    new FetchedJobDto
                    {
                        Job = DeserializeJob(job.InvocationData, job.Arguments),
                        State = job.StateName                        
                    }));
            }

            return new JobList<FetchedJobDto>(result);
        }
    }
}
