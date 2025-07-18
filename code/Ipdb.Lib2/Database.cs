﻿using Ipdb.Lib2.Cache;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ipdb.Lib2
{
    /// <summary>
    /// Database:  a collection of tables that can share transactions
    /// and are persisted in the same file.
    /// </summary>
    public class Database : IAsyncDisposable
    {
        private readonly IImmutableDictionary<string, object> _tableMap
            = ImmutableDictionary<string, object>.Empty;
        private long _recordId = 0;
        private volatile DatabaseState _databaseState = new();

        #region Constructors
        public Database(string databaseRootDirectory, params IEnumerable<TableSchema> schemas)
        {
            _tableMap = schemas
                .Select(s => new
                {
                    Table = CreateTable(s),
                    s.TableName
                })
                .ToImmutableDictionary(o => o.TableName, o => o.Table);
        }

        private object CreateTable(TableSchema schema)
        {
            var tableType = typeof(Table<>).MakeGenericType(schema.RepresentationType);
            var table = Activator.CreateInstance(
                tableType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                [this, schema],
                null);

            return table!;
        }
        #endregion

        ValueTask IAsyncDisposable.DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public Table<T> GetTable<T>(string tableName)
            where T : notnull
        {
            if (_tableMap.ContainsKey(tableName))
            {
                var table = _tableMap[tableName];

                if (table is Table<T> t)
                {
                    return t;
                }
                else
                {
                    var docType = table.GetType().GetGenericArguments().First();

                    throw new InvalidOperationException(
                        $"Table '{tableName}' doesn't have document type '{typeof(T).Name}':  " +
                        $"it has document type '{docType.Name}'");
                }
            }
            else
            {
                throw new InvalidOperationException($"Table '{tableName}' doesn't exist");
            }
        }

        internal async Task ForceDataManagementAsync(bool persistAll = false)
        {
            await Task.CompletedTask;
        }

        #region Record IDs
        public long NewRecordId()
        {
            return Interlocked.Increment(ref _recordId);
        }

        public IImmutableList<long> NewRecordIds(int recordCount)
        {
            var nextId = Interlocked.Add(ref _recordId, recordCount);

            return Enumerable.Range(0, recordCount)
                .Select(i => i + nextId - recordCount)
                .ToImmutableArray();
        }
        #endregion

        #region Transaction
        public TransactionContext CreateTransaction()
        {
            var transactionContext = new TransactionContext(this);

            ChangeDatabaseState(currentDbState =>
            {
                var newTransactionMap = currentDbState.TransactionMap.Add(
                    transactionContext.TransactionId,
                    new TransactionCache(
                        currentDbState.DatabaseCache,
                        new TransactionLog()));

                return new DatabaseState(currentDbState.DatabaseCache, newTransactionMap);
            });

            return transactionContext;
        }

        internal void ExecuteWithinTransactionContext(
            TransactionContext? transactionContext,
            Action<TransactionCache> action)
        {
            ExecuteWithinTransactionContext(
                transactionContext,
                tc =>
                {
                    action(tc);

                    return 0;
                });
        }

        internal T ExecuteWithinTransactionContext<T>(
            TransactionContext? transactionContext,
            Func<TransactionCache, T> func)
        {
            var temporaryTransactionContext = transactionContext == null
                ? CreateTransaction()
                : null;

            try
            {
                var transactionId = transactionContext?.TransactionId
                    ?? temporaryTransactionContext!.TransactionId;
                var transactionCache = _databaseState.TransactionMap[transactionId];
                var result = func(transactionCache);

                temporaryTransactionContext?.Complete();

                return result;
            }
            catch
            {
                temporaryTransactionContext?.Rollback();
                throw;
            }
        }

        internal void CompleteTransaction(long transactionId)
        {
            //  Fetch transaction cache
            var transactionCache = _databaseState.TransactionMap[transactionId];
            var newTransactionLog = transactionCache.UncommittedTransactionLog.ToImmutable();

            ChangeDatabaseState(currentDbState =>
            {   //  Remove it from map
                var newTransactionMap = currentDbState.TransactionMap.Remove(transactionId);

                if (transactionCache.UncommittedTransactionLog.IsEmpty)
                {
                    return new DatabaseState(currentDbState.DatabaseCache, newTransactionMap);
                }
                else
                {
                    var newDbCache = new DatabaseCache(
                        currentDbState.DatabaseCache.StorageBlockMap,
                        currentDbState.DatabaseCache.CommittedLogs.Add(newTransactionLog));

                    return new DatabaseState(newDbCache, newTransactionMap);
                }
            });
        }

        internal void RollbackTransaction(long transactionId)
        {
            ChangeDatabaseState(currentDbState =>
            {   //  Remove transaction from map (and forget about it)
                var newTransactionMap = currentDbState.TransactionMap.Remove(transactionId);

                return new DatabaseState(currentDbState.DatabaseCache, newTransactionMap);
            });
        }
        #endregion

        private DatabaseState ChangeDatabaseState(Func<DatabaseState, DatabaseState?> stateChange)
        {   //  Optimistically try to change the db state:  repeat if necessary
            var currentDbState = _databaseState;
            var newDbState = stateChange(currentDbState);

            if (newDbState == null)
            {
                return currentDbState;
            }
            else if (object.ReferenceEquals(
                currentDbState,
                Interlocked.CompareExchange(ref _databaseState, newDbState, currentDbState)))
            {
                return newDbState;
            }
            else
            {   //  Exchange fail, we retry
                return ChangeDatabaseState(stateChange);
            }
        }
    }
}