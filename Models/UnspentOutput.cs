using System;

namespace AngorFounderSpend
{
    public class UnspentOutput
    {
        public string TxId { get; set; }
        public int Vout { get; set; }
        // Store value in satoshis as a long
        public long Value { get; set; }
        // Add address and script type info
        public string Address { get; set; }
        public string ScriptType { get; set; }

        public string FounderKey { get; set; }
        
        // Helper method to get the output specifier (txid:vout)
        public string GetOutputId() => $"{TxId}:{Vout}";
    }
}
