using System;
using System.Collections.Generic;
using Hatifect.Flow.Application;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Diagnostics;

// Exact native UI acceptance owns this fake world; no game inventory, checkpoint or save is touched.
internal sealed class FlowUiAcceptanceWorld : IFlowApplication, IDisposable
{
    private readonly FlowRuntime _runtime;
    private readonly FlowApplication _owner;
    private readonly List<Action<long>> _subscriptions = new(4);
    private readonly List<Exception> _errors = new(1);
    private bool _seeded, _disposed;
    internal FlowUiAcceptanceWorld(bool secondWorld)
    {
        World = secondWorld ? "B" : "A";
        Quantity = secondWorld ? 13 : 7;
        _runtime = new FlowRuntime(new NetworkId(Id(1)));
        _owner = new FlowApplication(_runtime, operation =>
        {
            EffectAttempts++;
            if (FailNextOperation)
            {
                FailNextOperation = false;
                throw InjectedFailure;
            }
            operation(_runtime);
        }, error => _errors.Add(error));
    }

    internal string World { get; }
    internal int Quantity { get; }
    internal Guid Parcel => Id(6);
    internal int Subscribers => _subscriptions.Count;
    internal int ReadCalls { get; private set; }
    internal int Commands { get; private set; }
    internal int EffectAttempts { get; private set; }
    internal bool FailNextOperation { get; set; }
    internal bool FailNextUnsubscribe { get; set; }
    internal Exception InjectedFailure { get; } = new InvalidOperationException("Expected isolated Flow UI executor failure.");
    internal Exception UnsubscribeFailure { get; } = new InvalidOperationException("Expected isolated Flow UI unsubscribe failure.");
    internal IReadOnlyList<Exception> Errors => _errors.AsReadOnly();

    public event Action<long>? RevisionChanged
    {
        add
        {
            if (value is null) return;
            if (_disposed || _subscriptions.Count >= 16)
                throw new InvalidOperationException("The native UI fixture cannot retain another subscription.");
            _owner.RevisionChanged += value;
            _subscriptions.Add(value);
        }
        remove
        {
            if (value is null) return;
            int index = _subscriptions.LastIndexOf(value);
            if (index < 0) return;
            if (FailNextUnsubscribe)
            {
                FailNextUnsubscribe = false;
                throw UnsubscribeFailure;
            }
            _owner.RevisionChanged -= value;
            _subscriptions.RemoveAt(index);
        }
    }

    public FlowSnapshot ReadSnapshot() { ReadCalls++; return _owner.ReadSnapshot(); }
    public FlowCommandResult Execute(FlowParcelCommand command) { Commands++; return _owner.Execute(command); }
    internal string StationName(Guid station) => station == Id(2) ? World + " source"
        : station == Id(3) ? World + " destination" : throw new InvalidOperationException("A foreign station reached the UI fixture.");

    internal void Seed()
    {
        if (_disposed || _seeded) throw new InvalidOperationException("The UI fixture can be seeded only once while active.");
        var source = new StationId(Id(2));
        var destination = new StationId(Id(3));
        var cargo = new CargoId(Id(4));
        var shipment = new ShipmentId(Id(5));
        var manifest = new CargoManifest("(O)378", Quantity);
        var port = new InMemoryCargoPort();
        port.Seed(cargo, manifest);
        _runtime.AddStation(source, port);
        _runtime.AddStation(destination, new InMemoryCargoPort());
        _runtime.RegisterCargo(cargo, source, manifest);
        _runtime.AddLink(new LinkId(Id(7)), source, destination, 20, 8);
        _runtime.CreateShipment(shipment, source, destination, manifest,
            new ServicePolicy(ServiceClass.Standard, DeliveryGuarantee.Reserved));
        _runtime.SplitShipment(shipment, new ParcelId(Parcel), cargo);
        _seeded = true;
        _owner.Refresh();
    }

    internal void SetAvailability(FlowApplicationState state) => _owner.SetAvailability(state);
    internal void CloseApplication() => _owner.Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        if (_subscriptions.Count != 0)
            throw new InvalidOperationException("Flow UI subscribers must retire before their fixture owner.");
        _owner.Dispose();
        _disposed = true;
    }

    private Guid Id(int member) => new(member, World == "A" ? (short)1 : (short)2, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
