using System.Security.Cryptography;
using System.Text;
using PosSync.Core.Model;
using PosSync.Server.Sync;

namespace PosSync.Server.Admin;

/// <summary>
/// Seeds a synthetic menu for the demo stores. Item ids are derived from store + SKU, and a store that already has
/// a menu is skipped, so running the seed twice changes nothing. All names and prices are invented.
/// </summary>
internal sealed partial class DemoMenuSeeder(SyncStore store, ILogger<DemoMenuSeeder> logger)
{
    public static readonly string[] Stores = ["store-001", "store-002"];

    // (sku, name, category, price in kuruş: 1 TRY = 100 kuruş)
    internal static readonly (string Sku, string Name, string Category, long Price)[] Menu =
    [
        ("SOUP-01", "Lentil soup", "Starters", 9_000),
        ("SOUP-02", "Yoghurt soup", "Starters", 9_500),
        ("MEZE-01", "Hummus", "Starters", 11_000),
        ("MEZE-02", "Stuffed vine leaves", "Starters", 12_500),
        ("MAIN-01", "Grilled meatballs", "Mains", 32_000),
        ("MAIN-02", "Chicken shish", "Mains", 29_000),
        ("MAIN-03", "Lamb stew", "Mains", 38_000),
        ("MAIN-04", "Vegetable moussaka", "Mains", 26_000),
        ("MAIN-07", "Adana kebab", "Mains", 45_000),
        ("PIDE-01", "Cheese pide", "Mains", 22_000),
        ("DESSERT-01", "Rice pudding", "Desserts", 6_000),
        ("DESSERT-02", "Baklava", "Desserts", 7_000),
        ("DRINK-01", "Ayran", "Drinks", 2_500),
        ("DRINK-02", "Black tea", "Drinks", 1_500),
        ("DRINK-03", "Lemonade", "Drinks", 3_500),
        ("DRINK-04", "Turkish coffee", "Drinks", 4_000),
    ];

    public static Guid ItemId(string storeId, string sku) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{storeId}/{sku}"))[..16]);

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var written = 0;
        foreach (var storeId in Stores)
        {
            if (await store.HeadAsync(storeId, ct) > 0)
            {
                LogSkipped(storeId);
                continue;
            }

            foreach (var (sku, name, category, price) in Menu)
            {
                await store.AuthorAsync(storeId, SyncSchema.Entities.MenuItem, ItemId(storeId, sku),
                [
                    FieldOp.Set("sku", sku),
                    FieldOp.Set("name", name),
                    FieldOp.Set("category", category),
                    FieldOp.Set("price_minor", price.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    FieldOp.Set("available", "true"),
                ], ct);
                written++;
            }

            LogSeeded(storeId, Menu.Length);
        }

        return written;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Store {StoreId} already has data; menu seed skipped")]
    private partial void LogSkipped(string storeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded {Count} menu items for {StoreId}")]
    private partial void LogSeeded(string storeId, int count);
}
