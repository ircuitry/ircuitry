namespace Ircuitry.Core;

/// <summary>
/// Cozy bakery-themed default nicks - a flavour, a baked good, and a number, e.g. "BananaBread66" or
/// "StrawberryMuffin20". Used so each new bot/server starts with its own friendly handle instead of a
/// shared "ircuitry-bot".
/// </summary>
public static class BakeryNames
{
    private static readonly string[] Flavors =
    {
        "Banana", "Strawberry", "Almond", "Chocolate", "Vanilla", "Cinnamon", "Cherry", "Apple", "Pumpkin",
        "Lemon", "Maple", "Hazelnut", "Coconut", "Blueberry", "Raspberry", "Peach", "Caramel", "Honey",
        "Ginger", "Mango", "Pistachio", "Pecan", "Plum", "Apricot", "Orange", "Mocha", "Toffee", "Walnut",
        "Fig", "Nutmeg", "Cardamom", "Lavender", "Matcha", "Espresso", "Custard",
    };

    private static readonly string[] Items =
    {
        "Bread", "Muffin", "Scone", "Croissant", "Bun", "Loaf", "Tart", "Cookie", "Donut", "Cupcake",
        "Bagel", "Brioche", "Pretzel", "Waffle", "Pancake", "Pie", "Roll", "Danish", "Biscuit", "Cake",
        "Eclair", "Macaron", "Strudel", "Crumble", "Cobbler", "Churro", "Pastry", "Galette", "Wafer",
    };

    /// <summary>A fresh random nick like "CinnamonRoll42".</summary>
    public static string Random()
    {
        var r = System.Random.Shared;
        return Flavors[r.Next(Flavors.Length)] + Items[r.Next(Items.Length)] + r.Next(10, 100);
    }
}
