﻿using System;

namespace Hangfire.SQLite.Entities
{
    internal class SqlHash
    {
        public string Key { get; set; }
        public string Field { get; set; }
        public string Value { get; set; }
        public DateTime? ExpireAt { get; set; }
    }
}
