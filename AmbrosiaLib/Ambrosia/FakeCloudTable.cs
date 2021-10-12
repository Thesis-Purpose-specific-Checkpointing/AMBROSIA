using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Table;

namespace Ambrosia
{
    public class FakeCloudTable : CloudTable
    {
        private Dictionary<string, object> table;

        public CloudTableClient ServiceClient;
        
        public FakeCloudTable() : base(null)
        {
            table = new Dictionary<string, object>();
        }

        public override async Task<bool> CreateIfNotExistsAsync()
        {
            return true;
        }

        public override async Task<TableResult> ExecuteAsync(TableOperation operation)
        {
            var result = new TableResult();
            result.HttpStatusCode = 200;
            result.Etag = operation.Entity.ETag;
            
            switch (operation.OperationType)
            {
                case TableOperationType.Insert:
                case TableOperationType.InsertOrReplace:
                case TableOperationType.Replace:
                    if (table.ContainsKey(operation.Entity.RowKey))
                    {
                        table.Remove(operation.Entity.RowKey);
                    }
                    table.Add(operation.Entity.RowKey, operation.Entity);
                    break;
                case TableOperationType.Retrieve:
                    table.TryGetValue(operation.Entity.RowKey, out var value);
                    result.Result = value;
                    break;
                case TableOperationType.Delete:
                    table.Remove(operation.Entity.RowKey);
                    break;
            }

            return result;
        }
    }
}