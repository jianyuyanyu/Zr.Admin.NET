using Infrastructure;
using Infrastructure.Model;
using SqlSugar;
using System.Collections.Generic;
using System.Linq;
using ZR.CodeGenerator.Model;
using ZR.Model;

namespace ZR.CodeGenerator.Service
{
    public class CodeGeneraterService : DbProvider
    {
        /// <summary>
        /// 获取所有数据库名
        /// </summary>
        /// <returns></returns>
        public List<string> GetAllDataBases()
        {
            var db = GetSugarDbContext();
            //Oracle库特殊处理
            DbConfigs configs = AppSettings.Get<DbConfigs>(nameof(GenConstants.CodeGenDbConfig));
            if (configs.DbType == 3)
            {
                return string.IsNullOrEmpty(configs?.DbName) ? new List<string>() : new List<string> { configs.DbName };
            }
            var templist = db.DbMaintenance.GetDataBaseList(db);

            return templist.FindAll(f => !f.Contains("schema"));
        }

        /// <summary>
        /// 获取所有表
        /// </summary>
        /// <param name="dbName"></param>
        /// <param name="tableName"></param>
        /// <param name="pager"></param>
        /// <returns></returns>
        public List<DbTableInfo> GetAllTables(string dbName, string tableName, PagerInfo pager)
        {
            var tableList = GetSugarDbContext(dbName).DbMaintenance.GetTableInfoList(true);
            if (!string.IsNullOrEmpty(tableName))
            {
                tableList = tableList.Where(f => f.Name.ToLower().Contains(tableName.ToLower())).ToList();
            }
            //tableList = tableList.Where(f => !new string[] { "gen", "sys_" }.Contains(f.Name)).ToList();
            pager.TotalNum = tableList.Count;
            // 先排序再分页，保证跨页顺序稳定
            return tableList.OrderBy(f => f.Name).Skip(pager.PageSize * (pager.PageNum - 1)).Take(pager.PageSize).ToList();
        }

        /// <summary>
        /// 获取单表数据
        /// </summary>
        /// <param name="dbName"></param>
        /// <param name="tableName"></param>
        /// <returns></returns>
        public DbTableInfo GetTableInfo(string dbName, string tableName)
        {
            var tableList = GetSugarDbContext(dbName).DbMaintenance.GetTableInfoList(true);
            if (!string.IsNullOrEmpty(tableName))
            {
                return tableList.Where(f => f.Name.Equals(tableName, System.StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            }

            return null;
        }
        /// <summary>
        /// 获取列信息
        /// </summary>
        /// <param name="dbName"></param>
        /// <param name="tableName"></param>
        /// <returns></returns>
        public List<DbColumnInfo> GetColumnInfo(string dbName, string tableName)
        {
            return GetSugarDbContext(dbName).DbMaintenance.GetColumnInfosByTableName(tableName, true);
        }

        /// <summary>
        /// 获取Oracle所有序列（USER_SEQUENCES 为当前用户 schema 的序列，与库名无关）
        /// </summary>
        /// <returns></returns>
        public List<OracleSeq> GetAllOracleSeqs()
        {
            string sql = "SELECT * FROM USER_SEQUENCES";
            var seqs = GetSugarDbContext().Ado.SqlQuery<OracleSeq>(sql);

            return seqs.ToList();
        }
    }
}
