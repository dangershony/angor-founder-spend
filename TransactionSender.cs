using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using System.Text;
using System.Linq;

namespace AngorFounderSpend
{
    public class TransactionSender
    {
        private readonly HttpClient _httpClient;
        private readonly string _indexerUrl;
        private readonly Network _network;
        private readonly string _payoutAddress;

        public TransactionSender(HttpClient httpClient, string indexerUrl, Network network, string payoutAddress)
        {
            _httpClient = httpClient;
            _indexerUrl = indexerUrl;
            _network = network;
            _payoutAddress = payoutAddress;
        }

        public async Task<string> CreateAndSendTransaction(List<UnspentOutput> unspentOutputs, string walletWords)
        {
            try
            {
                Console.WriteLine("Creating transaction...");
                
                // Validate inputs
                if (unspentOutputs == null || !unspentOutputs.Any())
                {
                    throw new ArgumentException("No unspent outputs provided to spend");
                }
                
                if (string.IsNullOrEmpty(_payoutAddress))
                {
                    throw new ArgumentException("No payout address specified");
                }

                // Parse the payout address to ensure it's valid for the current network
                BitcoinAddress payoutBitcoinAddress;
                try
                {
                    payoutBitcoinAddress = BitcoinAddress.Create(_payoutAddress, _network);
                }
                catch (Exception ex)
                {
                    throw new ArgumentException($"Invalid payout address for {_network}: {ex.Message}");
                }

                // Generate the private key from wallet words
                Key privateKey = GeneratePrivateKeyFromWords(walletWords);
                if (privateKey == null)
                {
                    throw new InvalidOperationException("Failed to generate private key from wallet words");
                }

                // Create the transaction
                var transaction = Transaction.Create(_network);

                // Add inputs
                Money totalInput = Money.Zero;
                foreach (var utxo in unspentOutputs)
                {
                    var outpoint = new OutPoint(uint256.Parse(utxo.TxId), utxo.Vout);
                    transaction.Inputs.Add(new TxIn(outpoint));
                    totalInput += new Money(utxo.Value);
                }

                // Calculate fee (simple estimation: 1000 satoshis per input + 500 for output)
                Money fee = new Money(unspentOutputs.Count * 1000 + 500);
                
                // Calculate payout amount (total input minus fee)
                Money payoutAmount = totalInput - fee;
                
                if (payoutAmount <= Money.Zero)
                {
                    throw new InvalidOperationException("Transaction amount too small to cover fees");
                }

                // Add output to payout address
                transaction.Outputs.Add(new TxOut(payoutAmount, payoutBitcoinAddress.ScriptPubKey));

                // Sign each input
                for (int i = 0; i < unspentOutputs.Count; i++)
                {
                    var utxo = unspentOutputs[i];
                    
                    // Get the UTXO script pub key for signing
                    var scriptPubKey = await GetScriptPubKeyForUtxo(utxo.TxId, utxo.Vout);
                    
                    // Create coin for signing
                    var coin = new Coin(
                        fromTxHash: uint256.Parse(utxo.TxId),
                        fromOutputIndex: (uint)utxo.Vout,
                        amount: new Money(utxo.Value),
                        scriptPubKey: scriptPubKey
                    );
                    
                    // Sign the input with correct parameters
                    var signature = privateKey.Sign(transaction.GetSignatureHash(coin.GetScriptCode(), i, SigHash.All));
                    var signatureScript = new Script(
                        Op.GetPushOp(signature.ToDER()),
                        Op.GetPushOp(privateKey.PubKey.ToBytes())
                    );
                    transaction.Inputs[i].ScriptSig = signatureScript;
                }

                // Broadcast the transaction
                string txHex = transaction.ToHex();
                string txId = await BroadcastTransaction(txHex);
                
                Console.WriteLine($"Transaction created and broadcast successfully!");
                Console.WriteLine($"Transaction ID: {txId}");
                
                return txId;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating or broadcasting transaction: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }
                return null;
            }
        }
        
        private async Task<Script> GetScriptPubKeyForUtxo(string txId, int vout)
        {
            // Fetch the transaction to get the script pub key for the specific output
            var response = await _httpClient.GetStringAsync($"{_indexerUrl}/tx/{txId}");
            var txData = JsonConvert.DeserializeObject<dynamic>(response);
            
            var scriptPubKeyHex = (string)txData.vout[vout].scriptpubkey;
            return Script.FromHex(scriptPubKeyHex);
        }
        
        private async Task<string> BroadcastTransaction(string txHex)
        {
            // Create content to send
            var content = new StringContent(JsonConvert.SerializeObject(new { tx = txHex }), Encoding.UTF8, "application/json");
            
            // Send to the blockchain API
            var response = await _httpClient.PostAsync($"{_indexerUrl}/tx", content);
            response.EnsureSuccessStatusCode();
            
            // Parse the response to get the transaction ID
            var responseContent = await response.Content.ReadAsStringAsync();
            dynamic result = JsonConvert.DeserializeObject<dynamic>(responseContent);
            
            return result.txid;
        }
        
        private Key GeneratePrivateKeyFromWords(string words)
        {
            // This is a placeholder - you'll need to implement this with the provided code
            Console.WriteLine("Generating private key from wallet words...");
            
            // For now, return a dummy key (this would be replaced with actual implementation)
            return null;
        }
    }
}
