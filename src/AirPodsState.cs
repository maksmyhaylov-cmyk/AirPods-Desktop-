namespace AirPodsWin;

/// <summary>
/// Snapshot of what AirPods broadcast in Apple's "Proximity Pairing" BLE message
/// (manufacturer 0x004C, type 0x07). The message is unencrypted and is what
/// iPhones use for the battery pop-up.
/// </summary>
public sealed record AirPodsState(
    string Model,
    int? LeftBattery,
    int? RightBattery,
    int? CaseBattery,
    bool LeftCharging,
    bool RightCharging,
    bool CaseCharging,
    bool LeftInEar,
    bool RightInEar,
    bool LidOpen,
    short Rssi,
    DateTime Received)
{
    public bool AnyInEar => LeftInEar || RightInEar;

    public int? LowestPodBattery
    {
        get
        {
            if (LeftBattery is null) return RightBattery;
            if (RightBattery is null) return LeftBattery;
            return Math.Min(LeftBattery.Value, RightBattery.Value);
        }
    }

    public const ushort AppleCompanyId = 0x004C;

    private static readonly Dictionary<ushort, string> Models = new()
    {
        [0x0220] = "AirPods",
        [0x0F20] = "AirPods (2)",
        [0x1320] = "AirPods (3)",
        [0x1920] = "AirPods 4",
        [0x1B20] = "AirPods 4 (ANC)",
        [0x0E20] = "AirPods Pro",
        [0x1420] = "AirPods Pro 2",
        [0x2420] = "AirPods Pro 2 (USB-C)",
        [0x2720] = "AirPods Pro 3",
        [0x0A20] = "AirPods Max",
        [0x1F20] = "AirPods Max (USB-C)",
    };

    /// <summary>
    /// Parses the manufacturer-specific payload (without the company id).
    /// Returns null if it is not an AirPods proximity pairing message.
    /// </summary>
    public static AirPodsState? TryParse(byte[] data, short rssi)
    {
        // 0: type 0x07, 1: length 0x19, 2: prefix, 3-4: model, 5: status,
        // 6: pod batteries, 7: charging flags | case battery, 8: lid, 9: color ...
        if (data.Length < 10 || data[0] != 0x07 || data[1] != 0x19)
            return null;

        ushort modelId = (ushort)((data[3] << 8) | data[4]);
        if (!Models.TryGetValue(modelId, out var model))
            return null; // Beats or unknown Apple device

        byte status = data[5];
        // Bit 5 tells which pod sends the packet; values are relative to it.
        bool leftBroadcasting = (status & 0b0010_0000) != 0;

        int current = data[6] & 0x0F;
        int other = (data[6] >> 4) & 0x0F;
        int caseLevel = data[7] & 0x0F;
        int flags = (data[7] >> 4) & 0x0F;

        bool currentCharging = (flags & 0b0001) != 0;
        bool otherCharging = (flags & 0b0010) != 0;
        bool caseCharging = (flags & 0b0100) != 0;

        bool currentInEar = (status & 0b0000_0010) != 0;
        bool otherInEar = (status & 0b0000_1000) != 0;

        bool lidOpen = (data[8] & 0b0000_1000) == 0;

        int? Level(int raw) => raw <= 10 ? raw * 10 : null; // 15 = unknown

        bool isMax = model.StartsWith("AirPods Max", StringComparison.Ordinal);

        return new AirPodsState(
            Model: model,
            LeftBattery: Level(leftBroadcasting ? current : other),
            RightBattery: isMax ? null : Level(leftBroadcasting ? other : current),
            CaseBattery: isMax ? null : Level(caseLevel),
            LeftCharging: leftBroadcasting ? currentCharging : otherCharging,
            RightCharging: leftBroadcasting ? otherCharging : currentCharging,
            CaseCharging: caseCharging,
            LeftInEar: leftBroadcasting ? currentInEar : otherInEar,
            RightInEar: leftBroadcasting ? otherInEar : currentInEar,
            LidOpen: lidOpen,
            Rssi: rssi,
            Received: DateTime.Now);
    }
}
