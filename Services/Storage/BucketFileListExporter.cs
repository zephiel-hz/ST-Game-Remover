using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamPluginManager
{
    /// <summary>
    /// Utility untuk export list files dari S3 bucket ke CSV file
    /// Gunakan: dotnet run --project . -- export
    /// </summary>
    public class BucketFileListExporter
{
    private static readonly string SUPABASE_URL = SupabaseConfig.SupabaseUrl;
    private static readonly string SUPABASE_KEY = SupabaseConfig.SupabaseKey;
    private static readonly string STORAGE_URL = SupabaseConfig.StorageUrl;
    private static readonly string BUCKET_NAME = SupabaseConfig.StorageBucketName;

    public static async Task Main(string[] args)
    {
        Console.WriteLine("=== Supabase Bucket File Exporter ===");
        
        // Try list using Supabase REST API
        var files = await ListBucketFilesAsync();
        
        if (files.Count == 0)
        {
            Console.WriteLine("✗ No files found or unable to list bucket");
            return;
        }

        // Export to file_list.txt
        ExportToFile(files);
        
        Console.WriteLine($"✓ Exported {files.Count} files to file_list.txt");
        Console.WriteLine("Now run: python populate_hzmanifest.py");
    }

    private static async Task<List<string>> ListBucketFilesAsync()
    {
        var files = new List<string>();

        try
        {
            Console.WriteLine($"Listing files from {BUCKET_NAME}...");
            
            // Try Supabase REST API endpoint
            string url = $"{SUPABASE_URL}/rest/v1/rpc/list_bucket_files?bucket_name={BUCKET_NAME}";
            
            var client = SharedHttpClient.Instance;
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("apikey", SUPABASE_KEY);
            request.Headers.Add("Authorization", $"Bearer {SUPABASE_KEY}");

            var response = await client.SendAsync(request);
            string content = await response.Content.ReadAsStringAsync();

            Console.WriteLine($"Response: {response.StatusCode}");

            if (response.IsSuccessStatusCode)
            {
                // Parse JSON array
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                    {
                        if (item.TryGetProperty("name", out var nameElement))
                        {
                            string fileName = nameElement.GetString();
                            if (!string.IsNullOrEmpty(fileName) && !fileName.EndsWith("/"))
                            {
                                files.Add(fileName);
                                Console.WriteLine($"  ✓ {fileName}");
                            }
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine($"✗ Failed: {content}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Error: {ex.Message}");
        }

        return files;
    }

    private static void ExportToFile(List<string> files)
    {
        string outputPath = Path.Combine(Directory.GetCurrentDirectory(), "file_list.txt");
        File.WriteAllLines(outputPath, files);
        Console.WriteLine($"Exported to: {outputPath}");
    }
}
}
