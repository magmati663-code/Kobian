namespace Kobian.Providers.Sources.MsSql;

public enum SqlSupport
{
    Supported = 1,
    /// <summary>2014 SP3 / 2016 SP3: warunkowo (TLS 1.2, alert KB-3401 — D5.8).</summary>
    Conditional = 2,
    Unsupported = 3,
}

/// <summary>Polityka wersji SQL Server (D5.8). ProductMajorVersion: 12=2014, 13=2016, 14=2017, 15=2019, 16=2022, 17=2025.</summary>
public static class SqlVersionPolicy
{
    public static SqlSupport Classify(int productMajorVersion) => productMajorVersion switch
    {
        >= 14 => SqlSupport.Supported,
        12 or 13 => SqlSupport.Conditional,
        _ => SqlSupport.Unsupported,
    };

    /// <summary>EngineEdition = 4 → Express (brak natywnej kompresji kopii — Q24; nie wpływa na profil domyślny).</summary>
    public static bool IsExpress(int engineEdition) => engineEdition == 4;
}
