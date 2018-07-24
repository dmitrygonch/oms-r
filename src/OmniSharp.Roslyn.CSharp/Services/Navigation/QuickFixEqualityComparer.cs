using OmniSharp.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OmniSharp.Roslyn.CSharp.Services.Navigation
{

    internal class QuickFixEqualityComparer : IEqualityComparer<QuickFix>
    {
        public static readonly QuickFixEqualityComparer Instance = new QuickFixEqualityComparer();

        public bool Equals(QuickFix x, QuickFix y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }
            if (ReferenceEquals(x, null))
            {
                return false;

            }
            if (ReferenceEquals(y, null))
            {
                return false;

            }
            if (x.GetType() != y.GetType())
            {
                return false;
            }

            return
                x.Line == y.Line &&
                x.EndLine == y.EndLine &&
                x.Column == y.Column &&
                x.EndColumn == y.EndColumn &&
                x.FileName.Equals(y.FileName, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(QuickFix obj)
        {
            if (obj == null)
            {
                throw new ArgumentNullException(nameof(obj));
            }

            var hashComponents = new List<int>();
            hashComponents.Add(obj.Line.GetHashCode());
            hashComponents.Add(obj.EndLine.GetHashCode());
            hashComponents.Add(obj.Column.GetHashCode());
            hashComponents.Add(obj.EndColumn.GetHashCode());
            hashComponents.Add(obj.FileName.ToLowerInvariant().GetHashCode());
            unchecked
            {
                return 17 + hashComponents.Aggregate((aggregate, element) => aggregate * 23 + element);
            }
        }
    }
}
