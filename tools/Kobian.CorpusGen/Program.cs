using Kobian.Tests.Support;

if (args.Length != 2)
{
    Console.Error.WriteLine("Użycie: Kobian.CorpusGen <spec.json> <katalog_docelowy (pusty/nieistniejący)>");
    return 2;
}
var target = Path.GetFullPath(args[1]);
if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
{
    Console.Error.WriteLine("Katalog docelowy nie jest pusty — odmowa (generator nie nadpisuje).");
    return 3;
}
var spec = CorpusSpec.Load(File.ReadAllText(args[0]));
var files = new CorpusGenerator(spec).Generate(target);
File.WriteAllText(target + ".manifest.tsv", CorpusGenerator.Manifest(files));
Console.WriteLine($"{files.Count} plików, {files.Sum(f => f.Size)} B → {target}");
return 0;
