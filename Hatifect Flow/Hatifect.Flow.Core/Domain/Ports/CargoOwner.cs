using Hatifect.Flow.Domain.Identity;

namespace Hatifect.Flow.Domain.Ports;

internal sealed record CargoOwner
{
    public StationId? Station { get; }
    public ParcelId? Parcel { get; }
    public PortTransferId? Transfer { get; }

    private CargoOwner(StationId? station, ParcelId? parcel, PortTransferId? transfer = null)
    {
        Station = station;
        Parcel = parcel;
        Transfer = transfer;
    }

    public static CargoOwner AtStation(StationId station)
    {
        IdentityValue.Require(station.Value);
        return new(station, null);
    }

    public static CargoOwner InParcel(ParcelId parcel)
    {
        IdentityValue.Require(parcel.Value);
        return new(null, parcel);
    }

    public static CargoOwner InTransfer(PortTransferId transfer)
    {
        System.ArgumentNullException.ThrowIfNull(transfer);
        return new(null, null, transfer);
    }
}
