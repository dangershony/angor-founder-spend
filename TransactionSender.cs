using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Crypto; // Required for Hashes
using NBitcoin.DataEncoders; // Required for Bech32Encoder if used elsewhere
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

        // --- Key Derivation Methods ---

        // Returns the master key (m)
        private ExtKey CreateMasterPrivateKey(string words, string passphrase)
        {
            if (string.IsNullOrWhiteSpace(words))
            {
                throw new ArgumentNullException(nameof(words), "Wallet words cannot be empty.");
            }

            var mnemonic = new Mnemonic(words);
            string effectivePassphrase = passphrase ?? ""; 
            Console.WriteLine($"Deriving master key (m) using passphrase: {(string.IsNullOrEmpty(effectivePassphrase) ? "[empty]" : "[provided]")}");
            // DeriveExtKey returns the master key (m)
            var masterKey = mnemonic.DeriveExtKey(effectivePassphrase); 
            return masterKey;
        }

        private uint DeriveUniqueProjectIdentifier(string founderKeyHex)
        {
             if (string.IsNullOrWhiteSpace(founderKeyHex))
            {
                throw new ArgumentNullException(nameof(founderKeyHex), "Founder key cannot be empty.");
            }
            
            var key = new PubKey(founderKeyHex);
            var hashOfId = NBitcoin.Crypto.Hashes.DoubleSHA256(key.ToBytes());
            // Mask with int.MaxValue (0x7FFFFFFF) to ensure it's positive and fits in 31 bits for derivation?
            // This matches the example logic. Note: Standard BIP32 uses 31 bits for non-hardened derivation.
            var upi = (uint)(hashOfId.GetLow64() & int.MaxValue); 

            // The check for >= 2_147_483_648 seems redundant because of the & int.MaxValue mask, 
            // but we keep it for consistency with the provided example.
            if (upi >= 2_147_483_648) 
                throw new InvalidOperationException($"Derived UPI {upi} is out of the expected range for founder key {founderKeyHex}.");

            Console.WriteLine($"DeriveUniqueProjectIdentifier - founderKey = {founderKeyHex}, upi = {upi}");
            return upi;
        }

        // --- Updated CreateAndSendTransaction using TransactionBuilder ---

        public async Task<string> CreateAndSendTransaction(List<UnspentOutput> unspentOutputs, string walletWords, string passphrase)
        {
            try
            {
                Console.WriteLine("Creating transaction using TransactionBuilder...");
                
                // --- Validation (Unchanged) ---
                if (unspentOutputs == null || !unspentOutputs.Any()) throw new ArgumentException("No unspent outputs provided to spend");
                if (string.IsNullOrEmpty(_payoutAddress)) throw new ArgumentException("No payout address specified");
                BitcoinAddress payoutBitcoinAddress;
                try { payoutBitcoinAddress = BitcoinAddress.Create(_payoutAddress, _network); }
                catch (Exception ex) { throw new ArgumentException($"Invalid payout address for {_network}: {ex.Message}"); }

                // --- Key Generation ---
                // 1. Get the master private key (m)
                ExtKey masterPrivateKey = CreateMasterPrivateKey(walletWords, passphrase); 
                Console.WriteLine($"Using Master Key (m)");

                // 2. Determine the Angor Root Key based on network
                ExtKey angorRootKey;
                if (_network == Network.Main)
                {
                    // Mainnet: Angor root is the master key itself (m)
                    angorRootKey = masterPrivateKey;
                    Console.WriteLine("Using Mainnet derivation (Angor Root = m)");
                }
                else // Testnet or other networks
                {
                    // Testnet: Angor root is m/5'
                    angorRootKey = masterPrivateKey.Derive(5, hardened: true);
                    Console.WriteLine("Using Testnet derivation (Angor Root = m/5')");
                }

                // --- Prepare Coins and Keys ---
                Money totalInput = Money.Zero;
                List<Coin> coinsToSpend = new List<Coin>();
                List<Key> signingKeys = new List<Key>(); 

                foreach (var utxo in unspentOutputs)
                {
                    // 1. Get ScriptPubKey and create Coin
                    var scriptPubKey = await GetScriptPubKeyForUtxo(utxo.TxId, utxo.Vout);
                    var coin = new Coin(
                        fromTxHash: uint256.Parse(utxo.TxId),
                        fromOutputIndex: (uint)utxo.Vout,
                        amount: new Money(utxo.Value),
                        scriptPubKey: scriptPubKey
                    );
                    coinsToSpend.Add(coin);
                    totalInput += coin.Amount;

                    // 3. Derive corresponding private key from the Angor Root Key using UPI
                    if (string.IsNullOrWhiteSpace(utxo.FounderKey)) throw new InvalidOperationException($"FounderKey is missing for UTXO {utxo.TxId}:{utxo.Vout}");
                    uint upi = DeriveUniqueProjectIdentifier(utxo.FounderKey);
                    
                    // Derive the final key: AngorRoot / upi
                    Key inputPrivateKey = angorRootKey.Derive(upi).PrivateKey; 
                    string derivationPathString = (_network == Network.Main) ? $"m/{upi}" : $"m/5'/{upi}";
                    Console.WriteLine($"Deriving final key using path: {derivationPathString}");
                    
                    PubKey inputPubKey = inputPrivateKey.PubKey; 
                    signingKeys.Add(inputPrivateKey); 

                    // --- Verification Step ---
                    // Derive the expected address from the derived public key (assuming P2WPKH for Angor)
                    BitcoinWitPubKeyAddress derivedAddress = inputPubKey.WitHash.GetAddress(_network);
                    string derivedAddressString = derivedAddress.ToString();

                    Console.WriteLine($"Prepared input {coinsToSpend.Count - 1} (UTXO: {utxo.TxId}:{utxo.Vout}) using derived key for UPI {upi}.");
                    Console.WriteLine($"   Expected Address: {derivedAddressString}");
                    Console.WriteLine($"   UTXO Address:     {utxo.Address}");

                    // Compare derived address with the address stored in the UTXO object
                    if (string.IsNullOrEmpty(utxo.Address) || !derivedAddressString.Equals(utxo.Address, StringComparison.OrdinalIgnoreCase))
                    {
                        // Addresses don't match! Throw an error.
                        throw new InvalidOperationException($"Address mismatch for UTXO {utxo.TxId}:{utxo.Vout}! Derived key does not match UTXO address. Derived: '{derivedAddressString}', UTXO: '{utxo.Address}'. Check derivation logic or founder key.");
                    }
                    Console.WriteLine($"   Address verification successful.");
                    // --- End Verification Step ---
                }

                // --- Fee Calculation (Unchanged for now, builder can estimate but we override) ---
                Money fee = new Money(unspentOutputs.Count * 1000 + 500); // Example fee
                Money payoutAmount = totalInput - fee;
                if (payoutAmount <= Money.Zero) throw new InvalidOperationException("Transaction amount too small or negative after fee");

                // --- Build Transaction ---
                var builder = _network.CreateTransactionBuilder();
                
                // Add coins to spend and the keys required to spend them
                builder.AddCoins(coinsToSpend);
                builder.AddKeys(signingKeys.ToArray()); // Add all necessary private keys

                // Specify the destination and amount
                builder.Send(payoutBitcoinAddress, payoutAmount);

                // Set the fee explicitly (overrides automatic estimation)
                builder.SendFees(fee);

                // Build and sign the transaction
                Transaction transaction = builder.BuildTransaction(true); // `true` to sign

                // Verify the transaction (optional but recommended)
                if (!builder.Verify(transaction, out var errors))
                {
                    string errorMessages = string.Join("; ", errors.Select(e => e.ToString()));
                    throw new InvalidOperationException($"Transaction verification failed: {errorMessages}");
                }
                Console.WriteLine("Transaction built and verified successfully.");

                // --- Decode and Print Transaction Details ---
                Console.WriteLine("\n--- Decoded Transaction Details ---");
                // Use NBitcoin's ToString() for a detailed representation
                Console.WriteLine(transaction.ToString()); 
                Console.WriteLine("--- End Decoded Transaction Details ---");


                // --- Display Hex and Confirm Broadcast ---
                string txHex = transaction.ToHex();
                Console.WriteLine("\nTransaction Hex:");
                Console.WriteLine(txHex);

                Console.Write("\nDo you want to broadcast this transaction now? (yes/no): ");
                string confirmBroadcast = Console.ReadLine().ToLower();

                if (confirmBroadcast == "yes" || confirmBroadcast == "y")
                {
                    string txId = await BroadcastTransaction(txHex);
                    Console.WriteLine($"Transaction broadcast successfully!");
                    Console.WriteLine($"Transaction ID: {txId}");
                    return txId;
                }
                else
                {
                    Console.WriteLine("Transaction not broadcasted. You can broadcast the hex manually.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                 Console.WriteLine($"Error creating or broadcasting transaction: {ex.Message}");
                 Console.WriteLine($"Stack Trace: {ex.StackTrace}"); 
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

            // Add check for txData itself being null
            if (txData == null)
            {
                 throw new InvalidOperationException($"Failed to deserialize transaction data for {txId}. Response was null or invalid.");
            }
            
            // Ensure vout index is valid and vout object exists
            var outputs = txData.vout;
            if (outputs == null || vout >= outputs.Count || outputs[vout] == null)
            {
                throw new IndexOutOfRangeException($"Output index {vout} is out of range or output data is missing for transaction {txId}.");
            }

            // Directly access the scriptpubkey string property from the vout object
            string scriptPubKeyHex = (string)outputs[vout].scriptpubkey; // Access directly as string

             if (string.IsNullOrEmpty(scriptPubKeyHex))
            {
                 // Provide more context in the error
                 string voutJson = outputs[vout]?.ToString() ?? "null";
                 throw new InvalidOperationException($"ScriptPubKey hex string is missing or null for UTXO {txId}:{vout}. Vout object: {voutJson}");
            }
            return Script.FromHex(scriptPubKeyHex);
        }
        
        private async Task<string> BroadcastTransaction(string txHex)
        {
            // Create content to send: raw hex string with text/plain content type
            var content = new StringContent(txHex, Encoding.UTF8, "text/plain"); // Use text/plain
            
            // Send to the blockchain API
            Console.WriteLine($"Broadcasting transaction hex to {_indexerUrl}/tx");
            var response = await _httpClient.PostAsync($"{_indexerUrl}/tx", content); // Endpoint might just be /tx

            // Check response content for debugging even if status code is bad
            var responseContent = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Broadcast Response Status: {response.StatusCode}");
            Console.WriteLine($"Broadcast Response Content: {responseContent}");

            response.EnsureSuccessStatusCode(); // This will throw if status code is not 2xx
            
            // If successful, the response body is expected to be the txid directly
            // No need to parse JSON if the txid is the raw response body
            // dynamic result = JsonConvert.DeserializeObject<dynamic>(responseContent);
            // return result.txid;
            
            return responseContent; // Return the raw response content as the txid
        }
    }
}
