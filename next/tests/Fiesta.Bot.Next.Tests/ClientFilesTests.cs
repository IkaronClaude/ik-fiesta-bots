using Fiesta.Bot.Services;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

/// <summary>The shared client-file resolver: the client's path rules, case-blind.</summary>
public class ClientFilesTests
{
    private static string Tree()
    {
        var root = Path.Combine(Path.GetTempPath(), "cf-" + Guid.NewGuid().ToString("N")[..8]);
        void F(string rel) { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllBytes(p, [1]); }
        F("resmap/field/B_Rou/B_Rou.shbd");                 // a map's files named after its folder
        F("resmap/MHField/MHBasic/MHBasic.ini");
        F("resmap/Odd/Hidden/Hidden.shbd");                 // a kind outside the client's first four
        F("resmap/fieldTexture/Grass01.dds");                // a common folder
        F("resmap/MHField/MHBasic/Height.bmp");              // beside the file that names it
        F("ResMenu/ICON/Item_A.DDS");                        // case differs from the tables
        return root;
    }

    [Fact]
    public void A_path_named_inside_a_client_file_is_normalised_as_the_client_does()
    {
        Assert.Equal("resmap/field/x.dds", ClientFiles.Normalize(@".\resmap\field\x.dds"));
        Assert.Equal("resmap/MHField/a.bmp", ClientFiles.Normalize(@".resmap\MHField\a.bmp"));   // MHBasic.ini's form
        Assert.Equal("resmap/x.nif", ClientFiles.Normalize(@"\\..\resmap\x.nif"));
    }

    [Fact]
    public void Map_files_come_from_the_folder_found_in_the_client_order_case_blind()
    {
        var root = Tree();
        try
        {
            var f = new ClientFiles(root);
            Assert.EndsWith("resmap/field/B_Rou/B_Rou.shbd", f.MapFile("b_rou", ".SHBD"));
            Assert.EndsWith("resmap/Odd/Hidden/Hidden.shbd", f.MapFile("Hidden", ".shbd"));     // any other resmap folder
            Assert.Null(f.MapFile("Nowhere", ".shbd"));
            Assert.Contains(f.Misses, m => m.What.Contains("Nowhere") && m.Tried.Length >= 4);   // the candidates are kept
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_referenced_file_is_found_beside_its_naming_file_then_in_the_common_folders()
    {
        var root = Tree();
        try
        {
            var f = new ClientFiles(root);
            var ini = f.MapFile("MHBasic", ".ini")!;
            Assert.EndsWith("MHBasic/Height.bmp", f.Resolve(@".resmap\somewhere\Height.bmp", ini));   // leading '.' skipped, beside the .ini
            Assert.EndsWith("fieldTexture/Grass01.dds", f.Resolve(@"textures\grass01.DDS"));          // a common folder, case-blind
            Assert.EndsWith("ICON/Item_A.DDS", f.Icon("item_a"));                                      // resmenu/Icon/<file>.dds
            Assert.Null(f.Icon("-"));
        }
        finally { Directory.Delete(root, true); }
    }
}
