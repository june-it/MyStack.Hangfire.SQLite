﻿using System;

namespace Hangfire.SQLite.Entities
{
    internal class ServerData
    {
        public int WorkerCount { get; set; }
        public string[] Queues { get; set; }
        public DateTime? StartedAt { get; set; }
    }
}
