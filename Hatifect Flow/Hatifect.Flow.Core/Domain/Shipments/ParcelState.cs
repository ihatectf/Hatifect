namespace Hatifect.Flow.Domain.Shipments;

public enum ParcelState
{
    Created, Reserved, InTransit, Arrived, DeliveryRejected, DeliveryFaulted, Delivered, Cancelled,
    ExtractionUncertain, DeliveryUncertain,
    ReturnRequested, Returned, ReturnRejected, ReturnFaulted, ReturnUncertain
}
