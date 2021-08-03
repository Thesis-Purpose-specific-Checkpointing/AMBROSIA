namespace Ambrosia
{    
    public struct LongPair
    {
        public LongPair(long first,
            long second)
        {
            First = first;
            Second = second;
        }
        public long First { get; set; }
        public long Second { get; set; }
    }
}