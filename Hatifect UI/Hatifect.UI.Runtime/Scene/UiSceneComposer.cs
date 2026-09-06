using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Identity;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Projection;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Visual.Resolution;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Runtime.Scene;

internal sealed class UiSceneComposer
{
    internal static readonly UiSymbolId TerminalShellId = new("Hatifect.UI", "terminal/shell");
    private static readonly IReadOnlyDictionary<UiSymbolId, FoundationComponentKind> FoundationComponents =
        CreateFoundationCatalog();
    private readonly UiRegistrySnapshot? _registry;
    private UiTheme _theme;
    private readonly UiVisualResolver _visualResolver;
    private readonly UiFoundationVisuals _foundationVisuals;
    private readonly UiSymbolId _uniformItemSizing;
    private readonly UiSymbolId _adaptiveItemSizing;

    public UiSceneComposer(
        UiTheme theme,
        UiRegistrySnapshot? registry = null,
        UiSemanticCatalog? catalog = null,
        UiVisualResolver? visualResolver = null)
    {
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _registry = registry;
        UiSemanticCatalog semanticCatalog = catalog ?? UiSemanticCatalog.CreateFoundation();
        _visualResolver = visualResolver ?? new UiVisualResolver();
        _foundationVisuals = new UiFoundationVisuals(semanticCatalog);
        if (!semanticCatalog.TryGetPresentationProperty("itemSizing", out UiPropertySymbol? sizingProperty) ||
            sizingProperty == null ||
            !semanticCatalog.TryGetPropertyValue(sizingProperty, "Uniform", out UiEnumValueSymbol? uniform) ||
            uniform == null ||
            !semanticCatalog.TryGetPropertyValue(sizingProperty, "Adaptive", out UiEnumValueSymbol? adaptive) ||
            adaptive == null)
            throw new InvalidOperationException("Foundation item-sizing catalog values are not registered.");
        _uniformItemSizing = uniform.Id;
        _adaptiveItemSizing = adaptive.Id;
    }

    internal void SetTheme(UiTheme theme) => _theme = theme ?? throw new ArgumentNullException(nameof(theme));

    internal static int FoundationComponentCount => FoundationComponents.Count;

    public UiScene Compose(
        UiInvocationResult invocation,
        UiVisualDefinition? visual = null,
        UiInteractionSnapshot? interaction = null,
        string? locale = null,
        IReadOnlyList<UiExperienceDescriptor>? terminalSections = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return ComposeCaptured(invocation, visual, interaction, ResolveLocale(invocation, locale), terminalSections);
    }

    private UiScene ComposeCaptured(
        UiInvocationResult invocation,
        UiVisualDefinition? visual,
        UiInteractionSnapshot? interaction,
        string capturedLocale,
        IReadOnlyList<UiExperienceDescriptor>? terminalSections)
    {
        var reads = new UiPublicationReadScope(invocation.Experience);
        Dictionary<UiSymbolId, UiSemanticElementDefinition> elements = invocation.Experience.Elements
            .ToDictionary(element => element.Id);
        Dictionary<UiSymbolId, UiProjectedElement> projections = invocation.Projection.Elements
            .ToDictionary(projection => projection.Element);
        bool terminal = invocation.Descriptor.Host.Kind == UiHostKind.Terminal;
        Dictionary<UiSymbolId, List<UiSceneNode>> bySlot = terminal
            ? UiHostSlots.TerminalOrder.ToDictionary(slot => slot, _ => new List<UiSceneNode>())
            : new Dictionary<UiSymbolId, List<UiSceneNode>>();
        foreach (UiPlannedElement planned in invocation.Plan.Elements)
        {
            if (!elements.TryGetValue(planned.Element, out UiSemanticElementDefinition? element))
                throw new InvalidOperationException(
                    $"Plan element '{planned.Element}' is absent from Experience '{invocation.Experience.Id}'.");
            if (!projections.TryGetValue(planned.Element, out UiProjectedElement? projection))
                throw new InvalidOperationException(
                    $"Plan element '{planned.Element}' has no host-slot projection.");
            if (terminal && projection.HostSlot == UiHostSlots.Navigation)
                throw new InvalidOperationException(
                    $"Terminal section '{invocation.Experience.Id}' cannot project '{planned.Element}' into the " +
                    "shell-owned Navigation slot. Terminal navigation is derived from registered section descriptors.");
            Add(bySlot, projection.HostSlot, CreateElement(invocation, planned, element, visual, interaction, reads, capturedLocale));
        }

        AddContributions(invocation, visual, interaction, bySlot);
        if (terminal)
            AddTerminalNavigation(
                invocation,
                interaction,
                terminalSections ?? SnapshotAvailableTerminalSections(),
                bySlot);
        else if (terminalSections != null)
            throw new ArgumentException(
                "A Terminal availability snapshot can be supplied only for a Terminal invocation.",
                nameof(terminalSections));
        UiSymbolId shellOwner = terminal ? TerminalShellId : invocation.Experience.Id;
        IEnumerable<KeyValuePair<UiSymbolId, List<UiSceneNode>>> orderedSlots = terminal
            ? UiHostSlots.TerminalOrder.Select(slot => new KeyValuePair<UiSymbolId, List<UiSceneNode>>(slot, bySlot[slot]))
            : bySlot.OrderBy(item => item.Key, UiSymbolIdOrdinalComparer.Instance);
        UiSceneNode[] slots = orderedSlots
            .Select(item =>
            {
                UiSymbolId nodeId = shellOwner.Child($"scene/slot/{SlotName(item.Key)}");
                return (UiSceneNode)new UiSlotSceneNode(
                    nodeId,
                    UiSceneRoles.Slot,
                    Resolve(
                        UiSceneRoles.Slot,
                        UiSceneNodeKind.Slot,
                        nodeId,
                        invocation,
                        terminal ? null : visual,
                        interaction),
                    item.Key,
                    item.Value.ToArray());
            })
            .ToArray();
        UiSymbolId rootId = shellOwner.Child("scene/host");
        var root = new UiHostSceneNode(
            rootId,
            UiSceneRoles.Host,
            Resolve(
                UiSceneRoles.Host,
                UiSceneNodeKind.Host,
                rootId,
                invocation,
                terminal ? null : visual,
                interaction),
            invocation.Descriptor.Host,
            slots) { SemanticId = invocation.Experience.Id };
        return new UiScene(
            invocation.Experience.Id,
            terminal ? "Hatifect Terminal" : invocation.Experience.DisplayNameFor(capturedLocale),
            root,
            new UiSceneMeasurementContext(
                invocation.Plan.Host.Profile,
                capturedLocale,
                _theme.Id),
            reads.HasPublications ? nextInteraction => ComposeCaptured(invocation, visual, nextInteraction, capturedLocale, terminalSections) : null);
    }

    private static string ResolveLocale(UiInvocationResult invocation, string? locale)
    {
        if (invocation.Plan.Host.Environment is { } environment)
        {
            if (locale is not null && !string.Equals(locale, environment.Locale, StringComparison.Ordinal))
                throw new ArgumentException("Scene locale differs from the captured host environment.", nameof(locale));
            return environment.Locale;
        }
        return string.IsNullOrWhiteSpace(locale) ? CultureInfo.CurrentUICulture.Name : locale;
    }

    private UiSceneNode CreateElement(
        UiInvocationResult invocation,
        UiPlannedElement planned,
        UiSemanticElementDefinition element,
        UiVisualDefinition? visual,
        UiInteractionSnapshot? interaction,
        UiPublicationReadScope reads,
        string locale)
    {
        if (!FoundationComponents.TryGetValue(planned.Presentation, out FoundationComponentKind component))
            throw new InvalidOperationException($"No component is registered for Presentation '{planned.Presentation}'.");
        if (invocation.Experience.HasTextFormatter(element.Id) &&
            component is not (FoundationComponentKind.SelectionText or FoundationComponentKind.Inspector or FoundationComponentKind.StatusText))
            throw new InvalidOperationException($"Presentation '{planned.Presentation}' cannot format read-only text for element '{element.Id}'.");
        return component switch
        {
            FoundationComponentKind.Collection => Collection(
                invocation, planned, element, visual, interaction, reads, locale),
            FoundationComponentKind.TextInput => TextInput(invocation, element, visual, interaction, reads, locale),
            FoundationComponentKind.SelectionText when element.Source is IUiSemanticCollectionSource => Collection(
                invocation, planned, element, visual, interaction, reads, locale),
            FoundationComponentKind.SelectionText => Source(
                invocation, element, visual, interaction, UiSceneNodeKind.Text, UiSceneRoles.Text, reads, locale),
            FoundationComponentKind.Inspector => Source(
                invocation, element, visual, interaction, UiSceneNodeKind.Inspector, UiSceneRoles.Inspector, reads, locale),
            FoundationComponentKind.Form => Form(invocation, element, visual, interaction, reads, locale),
            FoundationComponentKind.StatusText => Source(
                invocation, element, visual, interaction, UiSceneNodeKind.Text, UiSceneRoles.Text, reads, locale),
            FoundationComponentKind.ActionBar => ActionBar(invocation, element, visual, interaction, reads, locale),
            _ => throw new InvalidOperationException($"Unsupported foundation component '{component}'.")
        };
    }

    private static IReadOnlyDictionary<UiSymbolId, FoundationComponentKind> CreateFoundationCatalog()
    {
        var result = new Dictionary<UiSymbolId, FoundationComponentKind>();
        Register(result, "Gallery", FoundationComponentKind.Collection);
        Register(result, "List", FoundationComponentKind.Collection);
        Register(result, "NavigationList", FoundationComponentKind.Collection);
        Register(result, "TextField", FoundationComponentKind.TextInput);
        Register(result, "FilterBar", FoundationComponentKind.TextInput);
        Register(result, "Value", FoundationComponentKind.SelectionText);
        Register(result, "Side", FoundationComponentKind.Inspector);
        Register(result, "Sheet", FoundationComponentKind.Inspector);
        Register(result, "Route", FoundationComponentKind.Inspector);
        Register(result, "Form", FoundationComponentKind.Form);
        Register(result, "Status", FoundationComponentKind.StatusText);
        Register(result, "ActionBar", FoundationComponentKind.ActionBar);
        return new ReadOnlyDictionary<UiSymbolId, FoundationComponentKind>(result);
    }

    private UiSceneNode Source(
        UiInvocationResult invocation,
        UiSemanticElementDefinition element,
        UiVisualDefinition? visual,
        UiInteractionSnapshot? interaction,
        UiSceneNodeKind kind,
        UiSymbolId fallbackRole,
        UiPublicationReadScope reads,
        string locale)
    {
        UiSymbolId nodeId = element.Id.Child("scene/component");
        UiSymbolId role = Role(invocation.Experience, element.Alias, fallbackRole);
        IUiSemanticSource captured = reads.Read(element.Source);
        string? displayText = invocation.Experience.HasTextFormatter(element.Id)
            ? invocation.Experience.FormatText(element.Id, captured.UntypedValue, locale) : null;
        return new UiSourceSceneNode(
            nodeId, kind, role,
            Resolve(role, kind, nodeId, invocation, visual, interaction),
            invocation.Experience.ElementLabelFor(element, locale), captured, displayText) { SemanticId = element.Id };
    }

    private UiSceneNode Collection(
        UiInvocationResult invocation,
        UiPlannedElement planned,
        UiSemanticElementDefinition element,
        UiVisualDefinition? visual,
        UiInteractionSnapshot? interaction,
        UiPublicationReadScope reads,
        string locale)
    {
        if (element.Source is not IUiSemanticCollectionSource collection)
            throw new InvalidOperationException(
                $"Collection element '{element.Id}' requires an IUiSemanticCollectionSource with stable item IDs.");
        UiSymbolId nodeId = element.Id.Child("scene/collection");
        UiSymbolId role = Role(invocation.Experience, element.Alias, UiSceneRoles.Collection);
        EnsureCollectionStateRecipesAreRenderOnly(role, visual);
        UiCollectionPresentationRecipe recipe = CollectionRecipe(
            planned.Presentation,
            invocation.Plan.CollectionRecipeFor(planned.Element));
        UiVisualResolution normal = Resolve(
            role, UiSceneNodeKind.Collection, nodeId, invocation, visual, interaction: null);
        IReadOnlyList<UiVisualStateRef> selectedState = new[] { UiVisualStates.Selected };
        UiVisualResolution selected = Resolve(
            role,
            UiSceneNodeKind.Collection,
            nodeId,
            invocation,
            visual,
            interaction: null,
            domainStates: selectedState);
        EnsureRenderOnlyState(nodeId, normal, selected, "Collection item states");

        // Capture policy values, not the mutable composer or the Experience/read scope.
        // Reconciliation may move focus after Compose without recapturing the publication.
        UiTheme theme = _theme;
        UiVisualResolver resolver = _visualResolver;
        UiFoundationVisuals foundation = _foundationVisuals;
        UiSymbolId profile = invocation.Plan.Host.Profile;
        var host = invocation.Descriptor.Host;
        var stateVisuals = new UiCollectionStateVisuals(nodeId, normal, selected, (domain, active) =>
            resolver.Resolve(new UiVisualContext(role, profile, domain, active), theme, visual,
                foundation.For(UiSceneNodeKind.Collection, host, domain, active)));

        var capturedCollection = reads.Read(collection) as IUiSemanticCollectionSnapshot;
        UiSymbolId? selectedId = capturedCollection?.SelectedItemId ??
            (capturedCollection is null ? (collection as IUiSelectableCollectionSource)?.SelectedItemId : null);
        UiSymbolId? selectedNode = selectedId is { } selectedItem
            ? ItemNodeId(nodeId, selectedItem)
            : null;
        var activeVisuals = new Dictionary<UiSymbolId, UiVisualResolution>();
        if (interaction != null)
        {
            foreach (UiSymbolId activeNode in ActiveItemNodes(interaction))
            {
                UiVisualResolution activeVisual = stateVisuals.Resolve(activeNode, activeNode == selectedNode, interaction);
                activeVisuals.Add(activeNode, activeVisual);
            }
        }
        return new UiCollectionSceneNode(
            nodeId,
            role,
            normal,
            selected,
            invocation.Experience.ElementLabelFor(element, locale),
            collection,
            recipe,
            activeVisuals,
            capturedCollection,
            stateVisuals) { SemanticId = element.Id };
    }

    private UiSceneNode TextInput(
        UiInvocationResult invocation,
        UiSemanticElementDefinition element,
        UiVisualDefinition? visual,
        UiInteractionSnapshot? interaction,
        UiPublicationReadScope reads,
        string locale)
    {
        UiSymbolId role = Role(invocation.Experience, element.Alias, UiSceneRoles.TextInput);
        UiSymbolId nodeId = element.Id.Child("scene/input");
        return new UiTextInputSceneNode(
            nodeId, role,
            Resolve(role, UiSceneNodeKind.TextInput, nodeId, invocation, visual, interaction),
            invocation.Experience.ElementLabelFor(element, locale), element.Source, reads.Read(element.Source)) { SemanticId = element.Id };
    }

    private UiSceneNode Form(
        UiInvocationResult invocation,
        UiSemanticElementDefinition element,
        UiVisualDefinition? visual,
        UiInteractionSnapshot? interaction,
        UiPublicationReadScope reads,
        string locale)
    {
        if (element.Source is not IUiSemanticFormSource form)
            return Source(
                invocation, element, visual, interaction, UiSceneNodeKind.Form, UiSceneRoles.Form, reads, locale);

        UiSymbolId labelRole = Role(invocation.Experience, "Field.Label", UiSceneRoles.Text);
        UiSymbolId inputRole = Role(invocation.Experience, "Field.Input", UiSceneRoles.TextInput);
        var children = new List<UiSceneNode>(form.Fields.Count * 2);
        foreach (UiSemanticFormField field in form.Fields)
        {
            IUiSemanticSource fieldValue = reads.Read(field.Value);
            UiSymbolId labelId = field.Id.Child("scene/label");
            children.Add(new UiTextSceneNode(
                labelId,
                labelRole,
                Resolve(labelRole, UiSceneNodeKind.Text, labelId, invocation, visual, interaction),
                field.Label) { SemanticId = field.Id });

            UiSymbolId inputId = field.Id.Child("scene/input");
            if (field.Kind is UiFormFieldKind.Toggle or UiFormFieldKind.Choice)
            {
                for (int index = 0; index < field.Options.Count; index++)
                {
                    UiFormOption option = field.Options[index];
                    UiSymbolId optionId = inputId.Child(index.ToString(CultureInfo.InvariantCulture));
                    bool selected = (string?)fieldValue.UntypedValue == option.Value;
                    var action = new UiActionDefinition(optionId, $"{field.Label}: {option.Label}{(selected ? " ✓" : string.Empty)}",
                        () => field.Value.Value = option.Value);
                    children.Add(new UiButtonSceneNode(optionId, UiSceneRoles.Button,
                        Resolve(UiSceneRoles.Button, UiSceneNodeKind.Button, optionId, invocation, visual, interaction,
                            domainStates: selected ? new[] { UiVisualStates.Selected } : null), action) { SemanticId = field.Id });
                }
            }
            else
                children.Add(new UiTextInputSceneNode(inputId, inputRole,
                    Resolve(inputRole, UiSceneNodeKind.TextInput, inputId, invocation, visual, interaction),
                    field.Label, field.Value, fieldValue) { SemanticId = field.Id });
            string? error = field.Error ?? (field.ValidationMessage is { } validation ? (string?)reads.Read(validation).UntypedValue : null);
            if (!string.IsNullOrWhiteSpace(error))
            {
                UiSymbolId errorId = field.Id.Child("scene/error");
                UiSymbolId errorRole = Role(invocation.Experience, "Field.Error", UiSceneRoles.Text);
                children.Add(new UiTextSceneNode(errorId, errorRole,
                    Resolve(errorRole, UiSceneNodeKind.Text, errorId, invocation, visual, interaction),
                    field.Error is null ? error : $"{field.Label}: {error}") { SemanticId = field.Id });
            }
        }

        UiSymbolId formId = element.Id.Child("scene/form");
        UiSymbolId formRole = Role(invocation.Experience, element.Alias, UiSceneRoles.Form);
        return new UiContainerSceneNode(
            formId,
            UiSceneNodeKind.Form,
            formRole,
            Resolve(formRole, UiSceneNodeKind.Form, formId, invocation, visual, interaction),
            children.ToArray(),
            invocation.Experience.ElementLabelFor(element, locale)) { SemanticId = element.Id };
    }

    private UiSceneNode ActionBar(
        UiInvocationResult invocation,
        UiSemanticElementDefinition element,
        UiVisualDefinition? visual,
        UiInteractionSnapshot? interaction,
        UiPublicationReadScope reads,
        string locale)
    {
        IReadOnlyList<UiActionDefinition> actions = reads.Read(element.Source).UntypedValue as IReadOnlyList<UiActionDefinition>
            ?? throw new InvalidOperationException($"Action element '{element.Id}' has an incompatible source.");
        UiSymbolId buttonRole = Role(invocation.Experience, "Action.Primary", UiSceneRoles.Button);
        UiSceneNode[] buttons = actions.Select(action =>
        {
            UiSymbolId nodeId = action.Id.Child("scene/button");
            return (UiSceneNode)Button(nodeId, buttonRole, action, invocation, visual, interaction,
                invocation.Experience.ActionTitleFor(action, locale));
        }).ToArray();
        UiSymbolId barRole = Role(invocation.Experience, element.Alias, UiSceneRoles.ActionBar);
        UiSymbolId barId = element.Id.Child("scene/action-bar");
        return new UiContainerSceneNode(
            barId, UiSceneNodeKind.ActionBar, barRole,
            Resolve(barRole, UiSceneNodeKind.ActionBar, barId, invocation, visual, interaction), buttons,
            invocation.Experience.ElementLabelFor(element, locale)) { SemanticId = element.Id };
    }

    private void AddContributions(
        UiInvocationResult invocation,
        UiVisualDefinition? visual,
        UiInteractionSnapshot? interaction,
        IDictionary<UiSymbolId, List<UiSceneNode>> bySlot)
    {
        if (_registry == null) return;
        foreach (UiContributionPointDescriptor point in _registry.ContributionPointsFor(invocation.Descriptor.Id))
        {
            UiSymbolId slot = UiSlotProjector.ProjectRegion(invocation.Plan.Host.HostKind, point.Region);
            if (invocation.Descriptor.Host.Kind == UiHostKind.Terminal && slot == UiHostSlots.Navigation)
                throw new InvalidOperationException(
                    $"Terminal section '{invocation.Experience.Id}' cannot contribute into the shell-owned " +
                    "Navigation slot. Register a Terminal section descriptor instead.");
            foreach (UiContributionDescriptor contribution in _registry.Contributions(point.Id))
            {
                UiSymbolId nodeId = contribution.Id.Child(
                    contribution is UiActionContributionDescriptor ? "scene/button" : "scene/route");
                UiSceneNode node = contribution switch
                {
                    UiActionContributionDescriptor action => Button(
                        nodeId, UiSceneRoles.Button, action.Action, invocation, visual, interaction),
                    UiRouteContributionDescriptor route => new UiRouteButtonSceneNode(
                        nodeId, UiSceneRoles.Button,
                        Resolve(UiSceneRoles.Button, UiSceneNodeKind.RouteButton, nodeId, invocation, visual, interaction),
                        route.Title, route.Route) { SemanticId = route.Route },
                    _ => throw new InvalidOperationException($"Unsupported contribution type '{contribution.GetType().Name}'.")
                };
                Add(bySlot, slot, node);
            }
        }
    }

    private void AddTerminalNavigation(
        UiInvocationResult invocation,
        UiInteractionSnapshot? interaction,
        IReadOnlyList<UiExperienceDescriptor> sections,
        IDictionary<UiSymbolId, List<UiSceneNode>> bySlot)
    {
        if (_registry == null)
            throw new InvalidOperationException("Terminal composition requires the frozen UI registry.");

        bool activeFound = false;
        foreach (UiExperienceDescriptor descriptor in sections)
        {
            if (!_registry.TryGetExperience(descriptor.Id, out UiExperienceDescriptor? registered) ||
                !ReferenceEquals(registered, descriptor) ||
                descriptor.Terminal == null ||
                descriptor.Host.Kind != UiHostKind.Terminal)
                throw new InvalidOperationException(
                    $"Terminal availability snapshot contains invalid section '{descriptor.Id}'.");
            UiSymbolId nodeId = descriptor.Id.Child("terminal/navigation-route");
            UiVisualResolution normal = Resolve(
                UiSceneRoles.Button,
                UiSceneNodeKind.RouteButton,
                nodeId,
                invocation,
                visual: null,
                interaction: interaction);
            bool current = descriptor.Id == invocation.Descriptor.Id;
            activeFound |= current;
            UiVisualResolution resolved = current
                ? Resolve(
                    UiSceneRoles.Button,
                    UiSceneNodeKind.RouteButton,
                    nodeId,
                    invocation,
                    visual: null,
                    interaction: interaction,
                    domainStates: new[] { UiVisualStates.Selected })
                : normal;
            if (current)
                EnsureRenderOnlyState(nodeId, normal, resolved, "Terminal navigation selection");
            Add(
                bySlot,
                UiHostSlots.Navigation,
                new UiRouteButtonSceneNode(nodeId, UiSceneRoles.Button, resolved, descriptor.Title, descriptor.Id, current, descriptor.Terminal.Icon)
                    { SemanticId = descriptor.Id });
        }
        if (!activeFound)
            throw new InvalidOperationException(
                $"Active Terminal section '{invocation.Descriptor.Id}' is absent from the availability snapshot.");
    }

    private IReadOnlyList<UiExperienceDescriptor> SnapshotAvailableTerminalSections()
    {
        if (_registry == null)
            throw new InvalidOperationException("Terminal composition requires the frozen UI registry.");
        return _registry.TerminalSections().Where(section => section.IsAvailable).ToArray();
    }

    private UiButtonSceneNode Button(UiSymbolId node, UiSymbolId role, UiActionDefinition action,
        UiInvocationResult invocation, UiVisualDefinition? visual, UiInteractionSnapshot? interaction, string? label = null)
    {
        if (action.Binding is not null && visual is not null)
        foreach (var recipe in visual.Recipes)
            if (recipe.Target == role && recipe.State is { } state &&
                (state == UiVisualStates.Disabled.Id || state == UiVisualStates.Focused.Id ||
                 state == UiVisualStates.Hover.Id || state == UiVisualStates.Pressed.Id) &&
                (recipe.Property.Effects & ~UiPropertyEffects.Render) != UiPropertyEffects.None)
                throw new InvalidOperationException($"Typed button state '{state}' changes layout geometry through '{recipe.Property.Name}'.");
        UiVisualResolution normal = Resolve(role, UiSceneNodeKind.Button, node, invocation, visual, null);
        UiTheme theme = _theme;
        UiVisualResolver resolver = _visualResolver;
        UiFoundationVisuals foundation = _foundationVisuals;
        UiSymbolId profile = invocation.Plan.Host.Profile;
        var host = invocation.Descriptor.Host;
        var states = new UiButtonStateVisuals(node, normal, active =>
            resolver.Resolve(new UiVisualContext(role, profile, null, active), theme, visual,
                foundation.For(UiSceneNodeKind.Button, host, null, active)), renderOnly: action.Binding is not null);
        bool enabled = action.Binding is not null || action.CanExecute;
        return new(node, role, states.Resolve(enabled, interaction), action, states, label) { SemanticId = action.Id };
    }

    private UiVisualResolution Resolve(
        UiSymbolId role,
        UiSceneNodeKind kind,
        UiSymbolId node,
        UiInvocationResult invocation,
        UiVisualDefinition? visual,
        UiInteractionSnapshot? interaction,
        bool enabled = true,
        IReadOnlyList<UiVisualStateRef>? domainStates = null,
        IReadOnlyList<UiVisualStateRef>? interactionStates = null)
    {
        IReadOnlyList<UiVisualStateRef>? resolvedInteractionStates =
            interactionStates ?? interaction?.StatesFor(node, enabled);
        return _visualResolver.Resolve(
            new UiVisualContext(
                role,
                invocation.Plan.Host.Profile,
                domainStates,
                resolvedInteractionStates),
            _theme,
            visual,
            _foundationVisuals.For(
                kind,
                invocation.Descriptor.Host,
                domainStates,
                resolvedInteractionStates));
    }

    private UiCollectionPresentationRecipe CollectionRecipe(
        UiSymbolId presentation,
        UiPlannedCollectionRecipe planned)
    {
        if (planned.ItemSizing != _uniformItemSizing && planned.ItemSizing != _adaptiveItemSizing)
            throw new InvalidOperationException(
                $"Collection Presentation '{presentation}' resolved unknown item sizing '{planned.ItemSizing}'.");
        bool gallery = presentation.LocalId.EndsWith("/Gallery", StringComparison.Ordinal);
        bool navigation = presentation.LocalId.EndsWith("/NavigationList", StringComparison.Ordinal);
        bool adaptive = planned.ItemSizing == _adaptiveItemSizing;
        if (navigation && adaptive)
            throw new InvalidOperationException("NavigationList is Uniform-only in v1.");
        return new UiCollectionPresentationRecipe(
            gallery ? UiCollectionLayoutKind.AdaptiveGrid : UiCollectionLayoutKind.List,
            gallery ? 4 : 8,
            gallery ? 3 : 1,
            planned.ItemSizing,
            adaptive,
            planned.Density,
            navigation);
    }

    private static IEnumerable<UiSymbolId> ActiveItemNodes(UiInteractionSnapshot interaction)
    {
        var unique = new HashSet<UiSymbolId>();
        if (interaction.Hovered is { } hovered && unique.Add(hovered)) yield return hovered;
        if (interaction.Focused is { } focused && unique.Add(focused)) yield return focused;
        if (interaction.Pressed is { } pressed && unique.Add(pressed)) yield return pressed;
    }

    private static UiSymbolId ItemNodeId(UiSymbolId collection, UiSymbolId item)
        => item;

    private static void EnsureRenderOnlyState(
        UiSymbolId node,
        UiVisualResolution normal,
        UiVisualResolution state,
        string context)
    {
        UiPropertyEffects invalidation = state.InvalidationFrom(normal);
        UiPropertyEffects forbidden = UiPropertyEffects.Recompose |
                                      UiPropertyEffects.Measure |
                                      UiPropertyEffects.Arrange;
        if ((invalidation & forbidden) != 0)
            throw new InvalidOperationException(
                $"{context} for '{node}' change layout geometry. State recipes may change render properties only.");
    }

    private static void EnsureCollectionStateRecipesAreRenderOnly(
        UiSymbolId role,
        UiVisualDefinition? visual)
    {
        if (visual == null) return;
        foreach (UiPropertyAssignmentIr recipe in visual.Recipes)
        {
            if (recipe.Target != role || recipe.State is not { } state ||
                state != UiVisualStates.Selected.Id &&
                state != UiVisualStates.Focused.Id &&
                state != UiVisualStates.Checked.Id)
                continue;
            UiPropertyEffects geometryEffects = recipe.Property.Effects & ~UiPropertyEffects.Render;
            if (geometryEffects == UiPropertyEffects.None) continue;
            throw new InvalidOperationException(
                $"Collection state '{state}' changes layout geometry through '{recipe.Property.Name}'; " +
                "Selected, Focused, and Checked recipes must be render-only.");
        }
    }

    private static UiSymbolId Role(UiExperienceDefinition experience, string name, UiSymbolId fallback)
        => experience.VisualRoles.FirstOrDefault(role => string.Equals(role.Name, name, StringComparison.Ordinal))?.Id ?? fallback;

    private static void Register(
        IDictionary<UiSymbolId, FoundationComponentKind> catalog,
        string name,
        FoundationComponentKind component)
        => catalog.Add(new UiSymbolId("Hatifect.UI", $"presentation/{name}"), component);

    private static void Add(IDictionary<UiSymbolId, List<UiSceneNode>> slots, UiSymbolId slot, UiSceneNode node)
    {
        if (!slots.TryGetValue(slot, out List<UiSceneNode>? nodes))
        {
            nodes = new List<UiSceneNode>();
            slots.Add(slot, nodes);
        }
        nodes.Add(node);
    }

    private static string SlotName(UiSymbolId slot)
        => slot.LocalId[(slot.LocalId.LastIndexOf('/') + 1)..];

    private enum FoundationComponentKind
    {
        Collection,
        TextInput,
        SelectionText,
        Inspector,
        Form,
        StatusText,
        ActionBar
    }
}
