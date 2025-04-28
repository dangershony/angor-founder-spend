using System;
using System.Net.Http;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.IO; // Required for file operations

namespace AngorFounderSpend
{
    class Program
    {
        // Constants
        private const string WalletWordPhrase = "area frost rapid guitar salon tower bless fly where inmate trouble daughter"; // This is testnet replace with actual mainnet phrase
        private const string MainnetIndexerUrl = "https://angor.shreddertest.xyz/api/v1";
        private const string TestnetIndexerUrl = "https://test.explorer.angor.io/api/v1";
        private const string PayoutAddress = ""; // Replace with actual address
        private const string CacheFileName = "angor_spend_cache.json"; // Cache file

        private static string _indexerUrl = TestnetIndexerUrl; // Default to testnet
        private static Network _network = Network.TestNet; // Default to testnet
        private static string _currencySymbol = "TBTC"; // Default to testnet symbol

        private static readonly HttpClient httpClient = new HttpClient();
        // Use Dictionary for unique UTXOs, key is "TxId:Vout"
        private static readonly Dictionary<string, UnspentOutput> unspentOutputs = new Dictionary<string, UnspentOutput>(); 
        private static long totalValue = 0;

        static async Task Main(string[] args)
        {
            Console.WriteLine("Angor Founder Spend Tool");
            Console.WriteLine("-------------------------");

            // Load cached UTXOs first
            LoadCache();
            
            // Parse command line arguments
            ParseCommandLineArgs(args);
            
            Console.WriteLine($"Using network: {(_network == Network.Main ? "Mainnet" : "Testnet")}");
            Console.WriteLine($"Currency: {_currencySymbol}");
            Console.WriteLine($"Indexer URL: {_indexerUrl}");

            // Display initially loaded cache summary
            DisplaySummary("Loaded from cache");

            // Ask user if they want to rescan or use cache
            Console.Write("Do you want to perform a full rescan? (yes/no, default: no): ");
            string rescanResponse = Console.ReadLine()?.ToLower() ?? "no";

            if (rescanResponse == "yes" || rescanResponse == "y")
            {
                 Console.WriteLine("Performing full rescan...");
                 unspentOutputs.Clear(); // Clear cache if rescanning
                 totalValue = 0;
                 await DiscoverProjects();
                 RecalculateTotalValue(); // Recalculate total after discovery
                 SaveCache(); // Save the newly discovered state
                 DisplaySummary("After full rescan");
            }
            else if (unspentOutputs.Count == 0)
            {
                 Console.WriteLine("Cache is empty, performing initial scan...");
                 await DiscoverProjects();
                 RecalculateTotalValue(); 
                 SaveCache(); 
                 DisplaySummary("After initial scan");
            }
            else
            {
                 Console.WriteLine("Using cached data. Run with 'yes' to force a full rescan.");
            }


            // Display total and ask about spending (using current state)
            await DisplayTotalAndSpendOption();

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        // --- Caching Methods ---

        private static void LoadCache()
        {
            try
            {
                if (File.Exists(CacheFileName))
                {
                    Console.WriteLine($"Loading cache from {CacheFileName}...");
                    string json = File.ReadAllText(CacheFileName);
                    var cacheData = JsonConvert.DeserializeObject<CacheData>(json);

                    unspentOutputs.Clear(); // Clear current before loading
                    if (cacheData?.UnspentOutputs != null)
                    {
                        foreach (var utxo in cacheData.UnspentOutputs)
                        {
                            string key = $"{utxo.TxId}:{utxo.Vout}";
                            unspentOutputs[key] = utxo; // Add to dictionary
                        }
                    }
                    RecalculateTotalValue(); // Calculate total from loaded cache
                    Console.WriteLine($"Loaded {unspentOutputs.Count} UTXOs from cache.");
                }
                else
                {
                    Console.WriteLine("Cache file not found. Will perform scan if needed.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading cache: {ex.Message}. Starting fresh.");
                unspentOutputs.Clear();
                totalValue = 0;
            }
        }

        private static void SaveCache()
        {
            try
            {
                Console.WriteLine($"Saving {unspentOutputs.Count} UTXOs to cache file {CacheFileName}...");
                var cacheData = new CacheData
                {
                    // Convert dictionary values back to list for saving
                    UnspentOutputs = unspentOutputs.Values.ToList() 
                };
                string json = JsonConvert.SerializeObject(cacheData, Formatting.Indented);
                File.WriteAllText(CacheFileName, json);
                Console.WriteLine("Cache saved successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving cache: {ex.Message}");
            }
        }

        private static void RecalculateTotalValue()
        {
            totalValue = unspentOutputs.Values.Sum(utxo => utxo.Value);
        }
        
        // Helper to display summary at different stages
        private static void DisplaySummary(string context)
        {
             Money currentTotalMoney = new Money(totalValue);
             Console.WriteLine($"\n----- Summary ({context}) -----");
             Console.WriteLine($"Total unspent value: {currentTotalMoney} ({currentTotalMoney.ToUnit(MoneyUnit.BTC)} {_currencySymbol})");
             Console.WriteLine($"Number of unspent outputs: {unspentOutputs.Count}");
        }


        // --- Existing Methods Modified for Caching ---
        
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
                        string founderKey = project["founderKey"].ToString(); // Get founder key here
                        
                        Console.WriteLine($"Project: {projectId} (Founder: {founderKey})");
                        
                        // Pass founderKey to FetchProjectInvestments
                        await FetchProjectInvestments(projectId, founderKey); 
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
                // Recalculate total value after discovery finishes
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error discovering projects: {ex.Message}");
            }
        }

        private static async Task FetchProjectInvestments(string projectId, string founderKey)
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
                    // Pass founderKey to CheckTransactionOutput
                    await CheckTransactionOutput(txId, founderKey); 
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching investments for project {projectId}: {ex.Message}");
            }
        }

        private static async Task CheckTransactionOutput(string txId, string founderKey)
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

                        Console.WriteLine($"Output {vout}: {amount} ({valueBtc} {_currencySymbol}), Address: {scriptPubKeyAddress}, Type: {scriptPubKeyType}, Spent: {isSpent}");

                        string key = $"{txId}:{vout}"; // Generate dictionary key

                        if (!isSpent)
                        {
                            // Add or update in the dictionary, including the Address
                            unspentOutputs[key] = new UnspentOutput
                            {
                                TxId = txId,
                                Vout = vout,
                                Value = valueSats,
                                FounderKey = founderKey,
                                Address = scriptPubKeyAddress, // Store the address
                                ScriptType = scriptPubKeyType // Store the script type
                            };
                        }
                        else
                        {
                             // If it's spent, ensure it's removed from our cache dictionary if it exists
                             if (unspentOutputs.ContainsKey(key))
                             {
                                 Console.WriteLine($"Output {key} is spent, removing from potential spend list.");
                                 unspentOutputs.Remove(key);
                             }
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
            // Use the endpoint that returns an array of spend statuses for all outputs
            string url = $"{_indexerUrl}/tx/{txId}/outspends"; 
            try
            {
                HttpResponseMessage response = await httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Warning/Error checking spend status array for {txId}. API response: {response.StatusCode}. URL: {url}. Content: {errorContent}");
                    // If we can't get the array, we can't determine the status for the specific vout
                    return false; // Cautiously assume unspent, but log indicates uncertainty
                }

                string jsonContent = await response.Content.ReadAsStringAsync();
                
                if (string.IsNullOrWhiteSpace(jsonContent))
                {
                     Console.WriteLine($"Warning: Empty response checking spend status array for {txId}. Assuming unspent. URL: {url}");
                     return false;
                }

                // Attempt to parse the JSON array
                try 
                {
                    // Deserialize into a list of dynamic objects (or a specific class if defined)
                    var spendStatusArray = JsonConvert.DeserializeObject<List<dynamic>>(jsonContent);
                    
                    // Check if the array is valid and the vout index is within bounds
                    if (spendStatusArray == null || vout < 0 || vout >= spendStatusArray.Count)
                    {
                        Console.WriteLine($"Warning: Spend status array for {txId} is null, empty, or vout {vout} is out of bounds (Array size: {spendStatusArray?.Count ?? 0}). Assuming unspent.");
                        return false;
                    }

                    // Access the element at the vout index
                    var outputStatus = spendStatusArray[vout];

                    // Check if the 'spent' property exists and is explicitly true
                    if (outputStatus != null && outputStatus.spent != null && outputStatus.spent == true)
                    {
                        return true; // Explicitly marked as spent at this index
                    }
                    else
                    {
                        // Assume unspent if 'spent' is false, null, or the property doesn't exist at this index
                        return false; 
                    }
                }
                catch (JsonException jsonEx)
                {
                     Console.WriteLine($"Error parsing JSON array response for {txId}: {jsonEx.Message}. Response: {jsonContent}. Assuming unspent.");
                     return false; // Error during parsing, assume unspent cautiously
                }
            }
            catch (HttpRequestException httpEx)
            {
                // Network or fundamental request error
                Console.WriteLine($"HTTP Error checking spend status array for {txId}: {httpEx.Message}. URL: {url}. Assuming unspent.");
                return false; // Network error, assume unspent cautiously
            }
             catch (Exception ex)
            {
                // Catch-all for other unexpected errors
                Console.WriteLine($"Unexpected Error checking spend status array for {txId}: {ex.Message}. Assuming unspent.");
                return false; // Unexpected error, assume unspent cautiously
            }
        }

        private static async Task DisplayTotalAndSpendOption()
        {
            if (unspentOutputs.Count > 0)
            {
                Console.Write($"\nThere are {unspentOutputs.Count} unspent outputs available. Do you want to spend some? (yes/no): ");
                string response = Console.ReadLine()?.ToLower() ?? "no";
                
                if (response == "yes" || response == "y")
                {
                    int countToSpend = 0;
                    bool validInput = false;
                    while (!validInput)
                    {
                        Console.Write($"How many inputs do you want to spend? (1-{unspentOutputs.Count}): ");
                        string countInput = Console.ReadLine();
                        if (int.TryParse(countInput, out countToSpend) && countToSpend > 0 && countToSpend <= unspentOutputs.Count)
                        {
                            validInput = true;
                        }
                        else
                        {
                            Console.WriteLine($"Invalid input. Please enter a number between 1 and {unspentOutputs.Count}.");
                        }
                    }

                    // Select the first 'countToSpend' UTXOs from the dictionary values
                    List<UnspentOutput> outputsToSpend = unspentOutputs.Values.Take(countToSpend).ToList();
                    
                    Console.WriteLine($"Selected {outputsToSpend.Count} inputs to spend:");
                    foreach (var utxo in outputsToSpend)
                    {
                         Money utxoAmount = new Money(utxo.Value);
                         Console.WriteLine($"- {utxo.TxId}:{utxo.Vout} ({utxoAmount.ToUnit(MoneyUnit.BTC)} {_currencySymbol})");
                    }

                    await CreateAndSendTransaction(outputsToSpend); 
                }
                else
                {
                    Console.WriteLine("Transaction creation cancelled.");
                }
            }
             else
             {
                 Console.WriteLine("\nNo unspent outputs available to spend.");
             }
        }

        // Modify CreateAndSendTransaction to remove only spent UTXOs from cache
        private static async Task CreateAndSendTransaction(List<UnspentOutput> outputsToSpend)
        {
            try
            {
                 if (outputsToSpend == null || !outputsToSpend.Any())
                 {
                      Console.WriteLine("No outputs selected to spend.");
                      return;
                 }

                // Calculate total value of selected inputs for display/confirmation
                long selectedValue = outputsToSpend.Sum(o => o.Value);
                Money selectedMoney = Money.Satoshis(selectedValue);

                Console.WriteLine($"\nPreparing to spend {outputsToSpend.Count} inputs with a total value of {selectedMoney} ({selectedMoney.ToUnit(MoneyUnit.BTC)} {_currencySymbol}).");
                Console.WriteLine($"Sending to address: {PayoutAddress}");
                
                // Ask for confirmation
                Console.Write("Proceed with transaction creation? (yes/no): ");
                string confirmResponse = Console.ReadLine().ToLower();
                if (confirmResponse != "yes" && confirmResponse != "y")
                {
                    Console.WriteLine("Transaction cancelled.");
                    return;
                }
                
                var transactionSender = new TransactionSender(
                    httpClient, 
                    _indexerUrl, 
                    _network, 
                    PayoutAddress);
                
                // Pass the selected list to the sender
                string txId = await transactionSender.CreateAndSendTransaction(outputsToSpend, WalletWordPhrase); 
                
                if (!string.IsNullOrEmpty(txId))
                {
                    // If transaction was successful, remove the spent UTXOs from the main dictionary
                    Console.WriteLine("Removing spent outputs from local state and saving cache...");
                    int removedCount = 0;
                    foreach (var spentUtxo in outputsToSpend)
                    {
                        string key = $"{spentUtxo.TxId}:{spentUtxo.Vout}";
                        if (unspentOutputs.Remove(key))
                        {
                             removedCount++;
                        }
                    }
                    Console.WriteLine($"Removed {removedCount} spent UTXOs from the list.");
                    RecalculateTotalValue(); // Update the total value based on remaining UTXOs
                    SaveCache(); // Save the updated state
                    DisplaySummary("After spending"); // Show summary of remaining UTXOs
                }
                 else
                 {
                      Console.WriteLine("Transaction broadcast was cancelled or failed. Local UTXO state remains unchanged.");
                 }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating transaction: {ex.Message}");
                 Console.WriteLine($"Stack Trace: {ex.StackTrace}"); // Keep stack trace for debugging
            }
        }
    }
}
