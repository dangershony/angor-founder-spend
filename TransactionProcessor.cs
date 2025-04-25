using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NBitcoin;

namespace AngorFounderSpend
{
    public class TransactionProcessor
    {
        private readonly HttpClient _httpClient;
        private readonly string _indexerUrl;
        private readonly List<UnspentOutput> _unspentOutputs = new List<UnspentOutput>();
        private long _totalValue = 0; // Changed from decimal to long for satoshis

        public TransactionProcessor(HttpClient httpClient, string indexerUrl)
        {
            _httpClient = httpClient;
            _indexerUrl = indexerUrl;
        }

        public Money TotalValue => new Money(_totalValue); // Updated property to return Money object
        public List<UnspentOutput> UnspentOutputs => _unspentOutputs;

        public async Task CheckTransactionOutput(string txId)
        {
            try
            {
                // Fetch transaction details
                var response = await _httpClient.GetStringAsync($"{_indexerUrl}/query/tx/{txId}");
                var txData = JsonConvert.DeserializeObject<JObject>(response);

                if (txData == null)
                {
                    Console.WriteLine($"Transaction {txId} not found");
                    return;
                }

                // Get the first output
                var outputs = txData["vout"];
                if (outputs == null || !outputs.Any())
                {
                    Console.WriteLine($"No outputs found for transaction {txId}");
                    return;
                }

                var firstOutput = outputs.First();
                // The value from API is in satoshis
                long valueSats = firstOutput["value"].Value<long>();
                int vout = firstOutput["n"].Value<int>();
                
                // Create Money object directly from satoshis
                Money amount = Money.Satoshis(valueSats);
                
                // Check if output is spent
                bool isSpent = await IsOutputSpent(txId, vout);
                
                Console.WriteLine($"Transaction {txId} output {vout}: {valueSats} sats ({amount.ToUnit(MoneyUnit.BTC)} BTC), Spent: {isSpent}");

                // Add to our total if unspent
                if (!isSpent)
                {
                    _totalValue += valueSats;
                    _unspentOutputs.Add(new UnspentOutput
                    {
                        TxId = txId,
                        Vout = vout,
                        Value = valueSats
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking transaction {txId}: {ex.Message}");
            }
        }

        private async Task<bool> IsOutputSpent(string txId, int vout)
        {
            try
            {
                // Check if the output is spent by querying for spending transactions
                var response = await _httpClient.GetStringAsync($"{_indexerUrl}/query/outspend/{txId}/{vout}");
                var spendData = JsonConvert.DeserializeObject<JObject>(response);

                return spendData != null && spendData["spent"].Value<bool>();
            }
            catch
            {
                // If we can't determine, assume it's unspent
                return false;
            }
        }
    }
}
