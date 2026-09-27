using Content.Shared.Imperial.Medieval.BookAbilities;
using Content.Shared.Imperial.Medieval.Knowledge;
using Robust.Shared.Player;

namespace Content.Client.Imperial.Medieval.BookAbilities;

/// <summary>Displays the server's options for a pending book ability.</summary>
public sealed class BookAbilityChoiceSystem : EntitySystem
{
    private BookAbilityChoiceWindow? _window;

    public override void Initialize()
    {
        SubscribeNetworkEvent<BookAbilityChoicesEvent>(OnChoices);
        SubscribeLocalEvent<LearnedKnowledgeComponent, LocalPlayerDetachedEvent>(OnDetached);
    }

    public override void Shutdown()
    {
        _window?.Close();
        base.Shutdown();
    }

    private void OnDetached(EntityUid uid, LearnedKnowledgeComponent component, LocalPlayerDetachedEvent args)
    {
        _window?.Close();
    }

    private void OnChoices(BookAbilityChoicesEvent args)
    {
        _window?.Close();
        var window = new BookAbilityChoiceWindow(args,
            choice => RaiseNetworkEvent(new BookAbilityChoiceSelectedEvent(args.RequestId, choice)));
        _window = window;
        window.OnClose += () =>
        {
            if (_window == window)
                _window = null;
            window.Dispose();
        };
        window.OpenCentered();
    }
}
