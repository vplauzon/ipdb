﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ipdb.Lib2.Query
{
    internal class ResultPredicate : IQueryPredicate
    {
        public ResultPredicate(IEnumerable<short> recordIndexes)
        {
            RecordIndexes = recordIndexes.ToImmutableArray();
        }

        public IImmutableList<short> RecordIndexes { get; }

        bool IQueryPredicate.IsTerminal => true;

        IQueryPredicate? IQueryPredicate.FirstPrimitivePredicate => null;

        IQueryPredicate? IQueryPredicate.Simplify(
            Func<IQueryPredicate, IQueryPredicate?> replaceFunc)
        {
            return null;
        }
    }
}