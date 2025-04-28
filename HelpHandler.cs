using System;

namespace AngorFounderSpend
{
    public static class HelpHandler
    {
        public static void DisplayHelp()
        {
            Console.WriteLine("\nAngor Founder Spend Tool");
            Console.WriteLine("-------------------------");
            Console.WriteLine("This tool scans the Angor blockchain (Testnet by default, or Mainnet)");
            Console.WriteLine("to find unspent transaction outputs (UTXOs) associated with Angor project");
            Console.WriteLine("founder keys derived from a provided mnemonic phrase.");
            Console.WriteLine("It allows consolidating these UTXOs into a single payout address.");
            Console.WriteLine("\nUsage: AngorFounderSpend.exe [options]");
            Console.WriteLine("\nOptions:");
            Console.WriteLine("  -mainnet        Use the Mainnet network and indexer. Defaults to Testnet.");
            // Console.WriteLine("  -passphrase \"<phrase>\"  Specify a BIP39 passphrase for wallet derivation."); // Keep commented if passphrase is hardcoded
            Console.WriteLine("  -?, -h, --help  Display this help message and exit.");
            Console.WriteLine("\nFunctionality:");
            Console.WriteLine(" - Caches found UTXOs in 'angor_spend_cache.json' to speed up subsequent runs.");
            Console.WriteLine(" - Prompts for a full rescan or to use cached data.");
            Console.WriteLine(" - If UTXOs are found, prompts to select a number to spend.");
            Console.WriteLine(" - Creates and optionally broadcasts a transaction to consolidate selected UTXOs.");
            Console.WriteLine("\nConfiguration (Hardcoded in Program.cs):");
            Console.WriteLine($" - Wallet Mnemonic: {Program.WalletWordPhraseForHelp}"); // Need to expose this safely
            Console.WriteLine($" - Wallet Passphrase: {(string.IsNullOrEmpty(Program.WalletPassphraseForHelp) ? "[empty]" : "[set]")}"); // Need to expose this safely
            Console.WriteLine($" - Payout Address: {Program.PayoutAddressForHelp}"); // Need to expose this safely
            Console.WriteLine($" - Testnet Indexer: {Program.TestnetIndexerUrlForHelp}"); // Need to expose this safely
            Console.WriteLine($" - Mainnet Indexer: {Program.MainnetIndexerUrlForHelp}"); // Need to expose this safely
        }
    }
}
