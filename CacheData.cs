using System.Collections.Generic;

namespace AngorFounderSpend
{
    public class CacheData
    {
        // Store as List for simpler JSON serialization
        public List<UnspentOutput> UnspentOutputs { get; set; } = new List<UnspentOutput>(); 
    }
}
