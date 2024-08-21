namespace Com.H.Collections.Generic.Depricated
{
    // extension methods for IEnumerable<dynamic> that are 
    // copied from Com.H library (github link below)
    // https://github.com/H7O/Com.H

    public static class Extensions
    {
        /// <summary>
        /// Takes an enumerable, fetches only the first value (to force
        /// SQL query evaluation which allows user custom Http errors to be thrown
        /// directly from SQL queries) then returns the first value 
        /// along with the rest of the enumerable items inside one consolidated enumerable
        /// </summary>
        /// <param name="enumerable"></param>
        /// <returns></returns>
        public static IEnumerable<dynamic> ToChamberedEnumerable(
            this IEnumerable<dynamic>? enumerable)
        {
            if (enumerable is null)
                return Enumerable.Empty<dynamic>();

            var enumerator = enumerable.GetEnumerator();
            if (enumerator.MoveNext())
            {
                return Enumerable.Concat(
                    new[] { enumerator.Current },
                    enumerator.RemainingItems());
            }
            return Enumerable.Empty<dynamic>();

        }

        /// <summary>
        /// Takes an enumerator and returns the rest of the items
        /// as enumerable
        /// </summary>
        /// <param name="enumerator"></param>
        /// <returns></returns>
        public static IEnumerable<dynamic> RemainingItems(
            this IEnumerator<dynamic>? enumerator)
        {
            if (enumerator is not null)
            {
                while (enumerator.MoveNext())
                {
                    yield return enumerator.Current;
                }
            }
        }



    }
}
