using System;
using System.Collections.Generic;
using AB.RevitMcp.Contracts.Json;

namespace AB.RevitMcp.Addin.Revit
{
    /// <summary>
    /// Every list-returning tool pages through this helper. Keeping responses small is not a
    /// nicety: an unpaged query on a real project returns tens of thousands of elements, blows the
    /// frame limit, and floods the AI's context with data it cannot use.
    /// </summary>
    public static class Paging
    {
        /// <summary>
        /// Wraps a full result set into a page envelope. The caller materialises all matches (so
        /// the totals are honest) and this slices out the requested window.
        /// </summary>
        public static JsonValue Page(List<JsonValue> all, int offset, int limit, string itemsKey = "items")
        {
            if (all == null) all = new List<JsonValue>();
            if (offset < 0) offset = 0;
            if (limit < 1) limit = 1;

            int start = Math.Min(offset, all.Count);
            int count = Math.Min(limit, all.Count - start);

            JsonValue items = JsonValue.NewArray();
            for (int i = 0; i < count; i++) items.Add(all[start + i]);

            JsonValue result = JsonValue.NewObject();
            result.Set(itemsKey, items);
            result.Set("returned", count);
            result.Set("total", all.Count);
            result.Set("offset", start);
            result.Set("limit", limit);

            bool hasMore = start + count < all.Count;
            result.Set("hasMore", hasMore);
            if (hasMore)
            {
                result.Set("nextOffset", start + count);
                result.Set("hint", "There are " + (all.Count - start - count) + " more items. " +
                                   "Call again with offset=" + (start + count) + " to continue.");
            }
            return result;
        }

        /// <summary>Case-insensitive "contains" that treats a null or empty needle as "match everything".</summary>
        public static bool Matches(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return true;
            if (string.IsNullOrEmpty(haystack)) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool EqualsCi(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
