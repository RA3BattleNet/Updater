using Microsoft.EntityFrameworkCore;
using Ra3.BattleNet.Updater.Server.PatchIndexGenerator.EFCoreModels;
using Ra3.BattleNet.Updater.Share.Log;
using System;
using System;
using System.Collections.Generic;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ra3.BattleNet.Updater.Server.PatchIndexGenerator
{

    public class UpdaterContext : DbContext
    {
        public DbSet<PatchIndexModel> PatchIndexes { get; set; }

        public string DbPath { get; }
        private const string DbName = "updater.db";
        public UpdaterContext()
        {
            //var folder = Environment.SpecialFolder.LocalApplicationData;
            //var path = Environment.GetFolderPath(folder);
            //DbPath = System.IO.Path.Join(path, DbName);
            var exePath = System.AppContext.BaseDirectory;
            DbPath = System.IO.Path.Combine(exePath, DbName);
            Logger.Info($"数据库路径：{DbPath}\n");
        }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite($"Data Source={DbPath}");
    }
}
