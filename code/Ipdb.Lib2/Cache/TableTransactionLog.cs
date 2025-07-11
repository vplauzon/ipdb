﻿using Ipdb.Lib2.Cache.CachedBlock;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Ipdb.Lib2.Cache
{
    internal record TableTransactionLog(
        BlockBuilder BlockBuilder,
        ImmutableHashSet<long>.Builder DeletedRecordIds)
    {
        public TableTransactionLog(TableSchema schema)
            : this(new BlockBuilder(schema), ImmutableHashSet<long>.Empty.ToBuilder())
        {
        }

        public bool IsEmpty => ((IBlock)BlockBuilder).RecordCount == 0 && !DeletedRecordIds.Any();

        public void AppendRecord(long recordId, object record)
        {
            BlockBuilder.AppendRecord(recordId, record);
        }

        public ImmutableTableTransactionLog ToImmutable()
        {
            return new ImmutableTableTransactionLog(
                new BlockBuilder(BlockBuilder),
                DeletedRecordIds.ToImmutableHashSet());
        }
    }
}