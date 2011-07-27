﻿using System;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Globalization;
using System.IO;
using System.Linq;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Announcers;
using FluentMigrator.Runner.Initialization;
using Samba.Infrastructure.Data;
using Samba.Infrastructure.Data.MongoDB;
using Samba.Infrastructure.Data.SQL;
using Samba.Infrastructure.Data.Text;
using Samba.Infrastructure.Settings;

namespace Samba.Persistance.Data
{
    public static class WorkspaceFactory
    {
        private static TextFileWorkspace _textFileWorkspace;
        private static MongoWorkspace _mongoWorkspace;
        private static string _connectionString = LocalSettings.ConnectionString;

        static WorkspaceFactory()
        {
            Database.SetInitializer(new Initializer());

            if (!string.IsNullOrEmpty(LocalSettings.ConnectionString))
            {
                if (LocalSettings.ConnectionString.EndsWith(".sdf"))
                {
                    Database.DefaultConnectionFactory =
                        new SqlCeConnectionFactory("System.Data.SqlServerCe.4.0", "", LocalSettings.ConnectionString);
                    return;
                }

                var cs = LocalSettings.ConnectionString;
                if (!cs.Trim().EndsWith(";"))
                    cs += ";";
                if (!cs.ToLower().Contains("multipleactiveresultsets"))
                    cs += " MultipleActiveResultSets=True;";
                if (!cs.ToLower(CultureInfo.InvariantCulture).Contains("user id") && (!cs.ToLower(CultureInfo.InvariantCulture).Contains("integrated security")))
                    cs += " Integrated Security=True;";
                if (cs.ToLower(CultureInfo.InvariantCulture).Contains("user id") && !cs.ToLower().Contains("persist security info"))
                    cs += " Persist Security Info=True;";
                Database.DefaultConnectionFactory =
                    new SqlConnectionFactory(cs);
            }

            if (string.IsNullOrEmpty(_connectionString) || _connectionString.EndsWith(".txt"))
                _textFileWorkspace = GetTextFileWorkspace();
            if (_connectionString.StartsWith("mongodb://"))
                _mongoWorkspace = GetMongoWorkspace();
        }

        public static IWorkspace Create()
        {
            if (_mongoWorkspace != null) return _mongoWorkspace;
            if (_textFileWorkspace != null) return _textFileWorkspace;
            return new EFWorkspace(new SambaContext(false));
        }

        public static IReadOnlyWorkspace CreateReadOnly()
        {
            if (_mongoWorkspace != null) return _mongoWorkspace;
            if (_textFileWorkspace != null) return _textFileWorkspace;
            return new ReadOnlyEFWorkspace(new SambaContext(true));
        }

        private static TextFileWorkspace GetTextFileWorkspace()
        {
            var fileName = _connectionString.EndsWith(".txt")
                ? _connectionString
                : LocalSettings.DocumentPath + "\\SambaData" + (LocalSettings.OverrideLanguage ? "_" + LocalSettings.CurrentLanguage : "") + ".txt";
            return new TextFileWorkspace(fileName, true);
        }

        private static MongoWorkspace GetMongoWorkspace()
        {
            return new MongoWorkspace(_connectionString);
        }

        public static void SetDefaultConnectionString(string cTestdataTxt)
        {
            _connectionString = cTestdataTxt;
            if (string.IsNullOrEmpty(_connectionString) || _connectionString.EndsWith(".txt"))
                _textFileWorkspace = GetTextFileWorkspace();
        }
    }

    public class Initializer : IDatabaseInitializer<SambaContext>
    {
        public void InitializeDatabase(SambaContext context)
        {
            if (!context.Database.Exists())
            {
                Create(context);
            }
#if DEBUG
            else if (!context.Database.CompatibleWithModel(false))
            {
                context.Database.Delete();
                Create(context);
            }
#else
            else
            {
                Migrate(context);
            }
#endif
            var version = context.ObjContext().ExecuteStoreQuery<long>("select top(1) Version from VersionInfo order by version desc").FirstOrDefault();
            LocalSettings.CurrentDbVersion = version;
        }

        private static void Create(CommonDbContext context)
        {
            context.Database.Create();
            context.ObjContext().ExecuteStoreCommand("CREATE TABLE VersionInfo (Version bigint not null)");
            GetMigrateVersions(context);
            LocalSettings.CurrentDbVersion = LocalSettings.DbVersion;
        }

        private static void GetMigrateVersions(CommonDbContext context)
        {
            for (var i = 0; i < LocalSettings.DbVersion; i++)
            {
                context.ObjContext().ExecuteStoreCommand("Insert into VersionInfo (Version) Values (" + (i + 1) + ")");
            }
        }

        private static void Migrate(CommonDbContext context)
        {
            if (!File.Exists(LocalSettings.DataPath + "\\migrate.txt")) return;

            var db = context.Database.Connection.ConnectionString.Contains(".sdf") ? "sqlserverce" : "sqlserver";

            using (IAnnouncer announcer = new TextWriterAnnouncer(Console.Out))
            {
                IRunnerContext migrationContext =
                    new RunnerContext(announcer)
                    {
                        Connection = context.Database.Connection.ConnectionString,
                        Database = db,
                        Target = LocalSettings.AppPath + "\\Samba.Persistance.DbMigration.dll"
                    };

                var executor = new TaskExecutor(migrationContext);
                executor.Execute();
            }

            File.Delete(LocalSettings.DataPath + "\\migrate.txt");
        }
    }
}
