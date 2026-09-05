namespace Hatifect.Flow.Domain.Scheduling;

internal enum OperationKind
{
    Reservation, Departure, Arrival, Transfer, Delivery, Cancellation,
    ReservationInvalidated, DeliveryRetry, PortUncertain, ExtractionRejected
}
