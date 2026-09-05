namespace Hatifect.Flow.Domain.Shipments;

internal enum ParcelState
{
    Created, Reserved, InTransit, Arrived, DeliveryRejected, DeliveryFaulted, Delivered, Cancelled,
    ExtractionUncertain, DeliveryUncertain
}
