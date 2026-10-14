namespace GameFerry.Engine.Disc;

public static class WiiConstants
{
    /// <summary>Size of one disc sector, which is also one encrypted partition cluster.</summary>
    public const int SectorSize = 0x8000;

    /// <summary>Hash header at the start of every partition cluster.</summary>
    public const int ClusterHashSize = 0x400;

    /// <summary>Decrypted payload carried by one partition cluster.</summary>
    public const int ClusterDataSize = SectorSize - ClusterHashSize;

    /// <summary>Magic word at disc offset 0x18 of a Wii disc.</summary>
    public const uint WiiMagic = 0x5D1C9EA3;

    /// <summary>Magic word at disc offset 0x1C of a GameCube disc.</summary>
    public const uint GameCubeMagic = 0xC2339F3D;

    public const long SingleLayerSize = 0x118240000L;
    public const long DualLayerSize = 0x1FB4E0000L;

    public const int SectorsPerLayer = 143432;

    /// <summary>Sectors WBFS reserves per disc (two layers), as in libwbfs.</summary>
    public const int MaxDiscSectors = SectorsPerLayer * 2;

    public const long PartitionTableOffset = 0x40000;

    /// <summary>End of the unencrypted area holding the disc header, partition table and region data.</summary>
    public const long HeaderAreaSize = 0x50000;
}
