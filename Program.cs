using System;
using System.Net.Http;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

namespace AngorFounderSpend
{
    class Program
    {
        // Constants
        private const string WalletWordPhrase = "YOUR_WALLET_WORD_PHRASE"; // Replace with actual phrase
        private const string MainnetIndexerUrl = "https://angor.shreddertest.xyz/api/v1";
        private const string TestnetIndexerUrl = "https://test.explorer.angor.io/api/v1";
        private const string PayoutAddress = "YOUR_PAYOUT_ADDRESS"; // Replace with actual address
        
        private static string _indexerUrl = TestnetIndexerUrl; // Default to testnet
        private static Network _network = Network.TestNet; // Default to testnet
        private static string _currencySymbol = "TBTC"; // Default to testnet symbol

        private static readonly HttpClient httpClient = new HttpClient();
        private static readonly List<UnspentOutput> unspentOutputs = new List<UnspentOutput>();
        private static long totalValue = 0;

        static async Task Main(string[] args)
        {
            Console.WriteLine("Angor Founder Spend Tool");
            Console.WriteLine("-------------------------");
            
            // Parse command line arguments
            ParseCommandLineArgs(args);
            
            Console.WriteLine($"Using network: {(_network == Network.Main ? "Mainnet" : "Testnet")}");
            Console.WriteLine($"Currency: {_currencySymbol}");
            Console.WriteLine($"Indexer URL: {_indexerUrl}");

            // Discover all projects
            await DiscoverProjects();

            // Display total and ask about spending
            await DisplayTotalAndSpendOption();

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
        
        private static void ParseCommandLineArgs(string[] args)
        {
            // Check if mainnet flag is present
            if (args.Any(arg => arg.Equals("-mainnet", StringComparison.OrdinalIgnoreCase)))
            {
                _indexerUrl = MainnetIndexerUrl;
                _network = Network.Main;
                _currencySymbol = "BTC"; // Update symbol for mainnet
                Console.WriteLine("Using mainnet configuration.");
            }
            else
            {
                _indexerUrl = TestnetIndexerUrl;
                _network = Network.TestNet;
                _currencySymbol = "TBTC"; // Update symbol for testnet
                Console.WriteLine("Using testnet configuration (default).");
            }
        }

        private static async Task DiscoverProjects()
        {
            try
            {
                Console.WriteLine("Discovering Angor projects...");
                
                int offset = 0;
                const int limit = 21;
                int totalProjects = 0;
                bool hasMore = true;
                
                while (hasMore)
                {
                    // Fetch projects from the indexer with pagination
                    var response = await httpClient.GetStringAsync($"{_indexerUrl}/query/Angor/projects?limit={limit}&offset={offset}");
                    var projects = JsonConvert.DeserializeObject<JArray>(response);
                    
                    if (projects == null || !projects.Any())
                    {
                        if (offset == 0)
                        {
                            Console.WriteLine("No projects found.");
                            return;
                        }
                        
                        // No more projects to fetch
                        hasMore = false;
                        continue;
                    }

                    // Process this batch
                    int batchCount = projects.Count;
                    totalProjects += batchCount;
                    
                    Console.WriteLine($"Found {batchCount} projects in batch (offset {offset})");
                    
                    foreach (var project in projects)
                    {
                        string projectId = project["projectIdentifier"].ToString();
                        string founderKey = project["founderKey"].ToString();
                        
                        Console.WriteLine($"Project: {projectId} (Founder: {founderKey})");
                        
                        // Now let's fetch investments for this project
                        await FetchProjectInvestments(projectId);
                    }
                    
                    // Prepare for the next batch
                    offset += limit;
                    
                    // If we got fewer than the limit, we're done
                    if (batchCount < limit)
                    {
                        hasMore = false;
                    }
                }
                
                Console.WriteLine($"Total projects processed: {totalProjects}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error discovering projects: {ex.Message}");
            }
        }

        private static async Task FetchProjectInvestments(string projectId)
        {
            try
            {
                Console.WriteLine($"Fetching investments for project {projectId}...");
                
                var response = await httpClient.GetStringAsync($"{_indexerUrl}/query/Angor/projects/{projectId}/investments");
                var investments = JsonConvert.DeserializeObject<JArray>(response);
                
                if (investments == null || !investments.Any())
                {
                    Console.WriteLine($"No investments found for project {projectId}.");
                    return;
                }

                Console.WriteLine($"Found {investments.Count} investments.");
                foreach (var investment in investments)
                {
                    string txId = investment["transactionId"].ToString();
                    await CheckTransactionOutput(txId);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching investments for project {projectId}: {ex.Message}");
            }
        }

        private static async Task CheckTransactionOutput(string txId)
        {
            try
            {
                // Fetch transaction details
                Console.WriteLine($"Checking transaction: {txId}");
                var response = await httpClient.GetStringAsync($"{_indexerUrl}/tx/{txId}");
                var txData = JsonConvert.DeserializeObject<JObject>(response);

                if (txData == null)
                {
                    Console.WriteLine($"Transaction {txId} not found");
                    return;
                }

                // Get outputs
                var outputs = txData["vout"] as JArray;
                if (outputs == null || outputs.Count == 0)
                {
                    Console.WriteLine($"No outputs found for transaction {txId}");
                    return;
                }

                // Process only the first output
                var firstOutput = outputs.FirstOrDefault();
                if (firstOutput == null)
                {
                    Console.WriteLine($"First output is null for transaction {txId}");
                    return;
                }

                try
                {
                    if (firstOutput["value"] != null)
                    {
                        // Get the value as decimal
                        long valueSats = firstOutput["value"].Value<long>();
                        
                        // Convert decimal value to Money using Money.Coins()
                        Money amount = Money.Satoshis(valueSats);
                        
                        int vout = firstOutput["n"]?.Value<int>() ?? 0;
                        
                        // Calculate BTC value for display (though the value should already be in BTC)
                        decimal valueBtc = amount.ToUnit(MoneyUnit.BTC);
                        
                        string scriptPubKeyAddress = firstOutput["scriptpubkey_address"]?.ToString() ?? "unknown";
                        string scriptPubKeyType = firstOutput["scriptpubkey_type"]?.ToString() ?? "unknown";

                        // Check if output is spent
                        bool isSpent = await IsOutputSpent(txId, vout);

                        Console.WriteLine($"First Output {vout}: {amount} ({valueBtc} {_currencySymbol}), Address: {scriptPubKeyAddress}, Type: {scriptPubKeyType}, Spent: {isSpent}");

                        // Add to our total if unspent
                        if (!isSpent)
                        {
                            // Add satoshi amount to our running total
                            totalValue += valueSats;
                            unspentOutputs.Add(new UnspentOutput
                            {
                                TxId = txId,
                                Vout = vout,
                                Value = valueSats
                            });
                        }
                    }
                    else
                    {
                        Console.WriteLine($"First output has no value for transaction {txId}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing first output: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking transaction {txId}: {ex.Message}");
                
                // Add more diagnostic information
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }
            }
        }

        private static async Task<bool> IsOutputSpent(string txId, int vout)
        {
            try
            {
                // Check if the output is spent by querying for spending transactions
                var response = await httpClient.GetStringAsync($"{_indexerUrl}/query/outspend/{txId}/{vout}");
                var spendData = JsonConvert.DeserializeObject<JObject>(response);

                return spendData != null && spendData["spent"].Value<bool>();
            }
            catch
            {
                // If we can't determine, assume it's unspent
                return false;
            }
        }

        private static async Task DisplayTotalAndSpendOption()
        {
            // Create Money object from total satoshis
            Money totalMoney = new Money(totalValue);
            
            Console.WriteLine("\n----- Summary -----");
            Console.WriteLine($"Total unspent value: {totalMoney} ({totalMoney.ToUnit(MoneyUnit.BTC)} {_currencySymbol})");
            Console.WriteLine($"Number of unspent outputs: {unspentOutputs.Count}");
            
            if (unspentOutputs.Count > 0)
            {
                Console.Write("\nDo you want to spend these coins? (yes/no): ");
                string response = Console.ReadLine().ToLower();
                
                if (response == "yes" || response == "y")
                {
                    await CreateAndSendTransaction();
                }
                else
                {
                    Console.WriteLine("Transaction creation cancelled.");
                }
            }
        }

        private static Key GeneratePrivateKeyFromWords(string words)
        {
            // Placeholder method - to be implemented later
            Console.WriteLine("Private key generation will be implemented later.");
            // This will be replaced with actual implementation once provided
            return null;
        }

        private static async Task CreateAndSendTransaction()
        {
            try
            {
                Console.WriteLine("Creating transaction...");
                
                // Display the UTXOs that will be spent
                Console.WriteLine("Will create a transaction with the following inputs:");
                foreach (var utxo in unspentOutputs)
                {
                    Money utxoAmount = new Money(utxo.Value);
                    Console.WriteLine($"- {utxo.TxId}:{utxo.Vout} ({utxoAmount} - {utxoAmount.ToUnit(MoneyUnit.BTC)} {_currencySymbol})");
                }
                
                Console.WriteLine($"Will send to address: {PayoutAddress}");
                
                // Ask for confirmation
                Console.Write("Proceed with transaction creation? (yes/no): ");
                string confirmResponse = Console.ReadLine().ToLower();
                if (confirmResponse != "yes" && confirmResponse != "y")
                {
                    Console.WriteLine("Transaction cancelled.");
                    return;
                }
                
                // Use the new TransactionSender class to create and broadcast the transaction
                var transactionSender = new TransactionSender(
                    httpClient, 
                    _indexerUrl, 
                    _network, 
                    PayoutAddress);
                
                string txId = await transactionSender.CreateAndSendTransaction(unspentOutputs, WalletWordPhrase);
                
                if (!string.IsNullOrEmpty(txId))
                {
                    // If transaction was successful, clear the list of unspent outputs
                    unspentOutputs.Clear();
                    totalValue = 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating transaction: {ex.Message}");
            }
        }
    }
}
