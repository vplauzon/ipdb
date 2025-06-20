﻿using Ipdb.Lib.Cache;
using Ipdb.Lib.DbStorage;
using Ipdb.Lib.Querying;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;

namespace Ipdb.Lib
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicConstructors)]
    public class Table<T>
    {
        private readonly TableSchema<T> _schema;
        private readonly ImmutableDictionary<string, IndexDefinition<T>> _indexByPath;
        private readonly IDatabaseService _databaseService;

        internal Table(TableSchema<T> schema, IDatabaseService databaseService)
        {
            _schema = schema;
            _indexByPath = _schema.Indexes.ToImmutableDictionary(i => i.PropertyPath);
            _databaseService = databaseService;
            QueryOp = new QueryOp<T>(_schema.Indexes
                .ToImmutableDictionary(i => i.PropertyPath, i => i));
        }

        public QueryOp<T> QueryOp { get; }

        #region Query
        public IImmutableList<T> Query(
            PredicateBase<T> predicate,
            TransactionContext? transactionContext = null)
        {
            _databaseService.ObserveBackgroundTasks();
            return TemporaryTransaction(
                transactionContext,
                transactionId =>
                {
                    var transactionCache = _databaseService.GetTransactionCache(transactionId);
                    var revisionDocuments = QueryRevisionDocuments(predicate, transactionCache);

                    return revisionDocuments
                    .Select(d => d.Document)
                    .ToImmutableArray();
                });
        }

        private IEnumerable<DocumentRevision<T>> QueryRevisionDocuments(
            PredicateBase<T> predicate,
            TransactionCache transactionCache)
        {
            var potentialRevisionIds = FindPotentialRevisionIds(
                predicate,
                transactionCache);
            var potentialDocuments = ListPotentialRevisionDocuments(
                potentialRevisionIds,
                transactionCache);
            var documents = predicate.FilterRevisionDocuments(potentialDocuments);

            return documents;
        }

        private IEnumerable<DocumentRevision<T>> ListPotentialRevisionDocuments(
            IImmutableSet<long> revisionIds,
            TransactionCache transactionCache)
        {
            //  From past transactions
            foreach (var log in transactionCache.DatabaseCache.TransactionLogs)
            {   //  Revision ids found in this transaction
                var foundIds = revisionIds.Intersect(log.NewDocuments.Keys);

                foreach (var id in foundIds)
                {
                    yield return new DocumentRevision<T>(id, Deserialize(log.NewDocuments[id]));
                }
                revisionIds = revisionIds.Except(foundIds);
            }
            //  From current transaction
            var currentIds = revisionIds.Intersect(
                transactionCache.TransactionLog.NewDocuments.Keys);

            if (currentIds.Count != revisionIds.Count)
            {
                throw new InvalidOperationException("Some revision IDs aren't found");
            }
            foreach (var id in currentIds)
            {
                yield return new DocumentRevision<T>(
                    id,
                    Deserialize(transactionCache.TransactionLog.NewDocuments[id]));
            }
        }

        private IImmutableSet<long> FindPotentialRevisionIds(
            PredicateBase<T> predicate,
            TransactionCache transactionCache)
        {
            while (true)
            {
                var primitivePredicate = predicate.FirstPrimitivePredicate;

                if (primitivePredicate == null)
                {
                    return ListPotentialRevisionIds(predicate);
                }
                else if (primitivePredicate is IIndexEqual<T> ie)
                {
                    var revisionIds = FindEqualHash(
                        transactionCache,
                        ie.IndexDefinition.PropertyPath,
                        ie.KeyHash);

                    predicate = predicate.Simplify(primitivePredicate, revisionIds)
                        ?? throw new InvalidOperationException("Predicate should simplify");
                }
                else
                {
                    throw new NotSupportedException(
                        $"Primitive '{primitivePredicate.GetType().Name}'");
                }
            }
        }

        private static IImmutableSet<long> ListPotentialRevisionIds(PredicateBase<T> predicate)
        {
            if (predicate is ResultPredicate<T> rp)
            {
                return rp.RevisionIds;
            }
            else
            {
                throw new NotSupportedException(
                    $"Result predicate:  '{predicate.GetType().Name}'");
            }
        }

        private IImmutableSet<long> FindEqualHash(
            TransactionCache transactionCache,
            string propertyPath,
            short keyHash)
        {
            var revisionIds = ImmutableHashSet<long>.Empty.ToBuilder();
            var key = new TableIndexHash(
                new TableIndexKey(_schema.TableName, propertyPath),
                keyHash);

            //  From past transactions
            foreach (var log in transactionCache.DatabaseCache.TransactionLogs)
            {   //  Remove documents that were removed in a transaction
                revisionIds.ExceptWith(log.DeletedDocuments);
                //  Match hashed index value
                if (log.NewIndexes.TryGetValue(key, out var pastRevisionIds))
                {
                    revisionIds.UnionWith(pastRevisionIds);
                }
            }
            //  From current transaction
            //  Remove documents that were removed in a transaction
            revisionIds.ExceptWith(transactionCache.TransactionLog.DeletedDocuments);
            //  Match hashed index value
            if (transactionCache.TransactionLog.NewIndexes.TryGetValue(
                key,
                out var newRevisionIds))
            {
                revisionIds.UnionWith(newRevisionIds);
            }

            return revisionIds.ToImmutable();
        }
        #endregion

        #region Append
        public void AppendDocument(T document, TransactionContext? transactionContext = null)
        {
            _databaseService.ObserveBackgroundTasks();
            TemporaryTransaction(
                transactionContext,
                transactionId =>
                {
                    var transactionCache = _databaseService.GetTransactionCache(transactionId);
                    var primaryIndex = _schema.Indexes.First();
                    var primaryKeyValue = primaryIndex.KeyExtractor(document);
                    var primaryKeyHash = primaryIndex.HashExtractor(document);
                    var deletedCount = DeleteDocumentsInternal(
                        new EqualOpPredicate<T>(
                            _schema.Indexes.First(),
                            primaryKeyValue,
                            primaryKeyHash),
                        transactionCache);
                    var revisionId = _databaseService.GetNewDocumentRevisionId();
                    var serializedDocument = Serialize(document);

                    transactionCache.TransactionLog.AppendDocument(revisionId, serializedDocument);
                    foreach (var index in _schema.Indexes)
                    {
                        transactionCache.TransactionLog.AppendIndexValue(
                            new TableIndexHash(
                                new TableIndexKey(_schema.TableName, index.PropertyPath),
                                index.HashExtractor(document)),
                            revisionId);
                    }
                });
        }
        #endregion

        #region Delete
        public int DeleteDocuments(
            PredicateBase<T> predicate,
            TransactionContext? transactionContext = null)
        {
            _databaseService.ObserveBackgroundTasks();

            return TemporaryTransaction(
                transactionContext,
                transactionId =>
                {
                    var transactionCache = _databaseService.GetTransactionCache(transactionId);

                    return DeleteDocumentsInternal(predicate, transactionCache);
                });
        }

        private int DeleteDocumentsInternal(
            PredicateBase<T> predicate,
            TransactionCache transactionCache)
        {
            var documents = QueryRevisionDocuments(predicate, transactionCache);
            var deleteCount = 0;

            foreach (var doc in documents)
            {
                transactionCache.TransactionLog.DeleteDocument(doc.RevisionId);
                foreach (var index in _schema.Indexes)
                {
                    var indexHash = index.HashExtractor(doc.Document);

                    transactionCache.TransactionLog.DeleteIndexValue(
                        new TableIndexHash(
                            new TableIndexKey(_schema.TableName, index.PropertyPath),
                            indexHash),
                        doc.RevisionId);
                }
                ++deleteCount;
            }

            return deleteCount;
        }
        #endregion

        #region Serialization
        private byte[] Serialize(T document)
        {
            var bufferWriter = new ArrayBufferWriter<byte>();

            using (var writer = new Utf8JsonWriter(bufferWriter))
            {
                JsonSerializer.Serialize(writer, document);
            }

            return bufferWriter.WrittenMemory.ToArray();
        }

        private static T Deserialize(byte[] buffer)
        {
            var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(buffer));

            return JsonSerializer.Deserialize<T>(ref reader)
                ?? throw new JsonException(
                    $"Failed to deserialize document of type {typeof(T).Name}");
        }
        #endregion

        #region Temporary Transaction
        private void TemporaryTransaction(
            TransactionContext? transactionContext,
            Action<long> action)
        {
            TemporaryTransaction(
                transactionContext,
                transactionId =>
                {
                    action(transactionId);

                    return 0;
                });
        }

        private R TemporaryTransaction<R>(
            TransactionContext? transactionContext,
            Func<long, R> func)
        {
            var implicitContext = transactionContext ?? _databaseService.CreateTransaction();
            var transactionId = transactionContext != null
                ? transactionContext.TransactionId
                : implicitContext.TransactionId;

            try
            {
                var returnValue = func(transactionId);

                implicitContext?.Complete();

                return returnValue;
            }
            finally
            {
                ((IDisposable)implicitContext).Dispose();
            }
        }
        #endregion
    }
    }