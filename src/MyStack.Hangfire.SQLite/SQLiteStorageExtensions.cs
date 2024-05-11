using Hangfire.Annotations;
using System;

namespace Hangfire.SQLite
{
    public static class SQLiteStorageExtensions
    {
        public static IGlobalConfiguration<SQLiteStorage> UseSQLiteStorage(
            [NotNull] this IGlobalConfiguration configuration,
            [NotNull] string nameOrConnectionString)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (nameOrConnectionString == null) throw new ArgumentNullException(nameof(nameOrConnectionString));

            var storage = new SQLiteStorage(nameOrConnectionString);
            return configuration.UseStorage(storage);
        }

        public static IGlobalConfiguration<SQLiteStorage> UseSQLiteStorage(
            [NotNull] this IGlobalConfiguration configuration,
            [NotNull] string nameOrConnectionString,
            [NotNull] SQLiteStorageOptions options)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (nameOrConnectionString == null) throw new ArgumentNullException(nameof(nameOrConnectionString));
            if (options == null) throw new ArgumentNullException(nameof(options));

            var storage = new SQLiteStorage(nameOrConnectionString, options);
            return configuration.UseStorage(storage);
        }
    }
}
