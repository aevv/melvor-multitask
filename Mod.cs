// A "Simultaneous Actions" slider (1 to 5) on the Mods page of the game's
// settings. With it above 1, starting a skill no longer stops the one already running.
//
// The game is built for this and holds itself back in one place. ActiveActionManager
// (GamesByMalcs.Melvor2.Components.Actions) keeps a list of running actions, saves and
// loads the whole list, and every action component ticks on its own whether or not
// another is running. The limit of one is ActionComponent.TryStart, which calls
// ActiveActionManager.TryStopActive (stop everything running) before it starts, and
// starts only if that worked. Nothing else calls TryStopActive. A Harmony prefix
// replaces it: with room left it stops nothing; when full it stops the action that has
// been running longest, so the newest ones are kept. At 1 the game's own method runs.
//
// One action component per skill, and TryStart is only reached when that component is
// not running (every caller checks IsStarted), so two things in one skill still can't
// run together, and the action being started is never in the list being trimmed.
//
// Lowering the setting stops the longest-running actions over the new limit there and
// then. A save made with several actions running loads in the unmodded game with all of
// them still going (it reads the list as written) until something new is started.
//
// Time away needs nothing: it runs the same ticks (Main.GameState.RunOfflineTicks calls
// Game.PropagateTick), and the summary has a panel for every skill that did something.
// That summary (offline_snapshot_modal.tscn) is laid out far wider than the screen, so
// its row of panels never scrolls: from about five panels on, the outer ones are cut
// off at the screen's edges. FitSummary holds the row to the width that is visible, and
// then the scroll container the game already put around it scrolls.
//
// What the game shows, and what is changed so it copes with more than one:
//  - The sidebar (GameSidebarLegacy) puts a progress ring on a skill when its action
//    starts, but after a save is loaded or time away it only does so for the first
//    running action (Update binds ActiveActions[0]). A postfix does the rest.
//  - The footer bar (GameFooter.Update) shows the action started last. A prefix makes
//    it the running action of the skill page being looked at, and when that page has
//    none, the one it showed before. GameView.SetCurrentPage is where pages change.
//    The "other players training this" count follows the footer, so the player is only
//    ever in one presence stream on the game's server, as in the unmodded game.
//  - Skill pages read the running action when they are opened, and need nothing.
using System;
using System.Linq;
using System.Reflection;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.GameMenus;
using GamesByMalcs.Melvor2.GameMenus.Footer;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using GamesByMalcs.Melvor2.GameMenus.SidebarLegacy;
using Godot;
using HarmonyLib;
using Umitoo;

namespace Multitask;

public sealed class MultitaskMod : Mod
{
    private static Setting<double> s_limit = null!;
    // the manager of the game in play (a new one is made for each save loaded)
    private static WeakReference<ActiveActionManager>? s_manager;

    private static int Limit => (int)Math.Round(s_limit.Value);

    public MultitaskMod()
    {
        s_limit = Settings.Slider("limit", "Simultaneous Actions", min: 1, max: 5, step: 1, initial: 2,
            description: "How many skills can be trained at the same time. Starting one more stops the one that has run longest.");
        s_limit.Changed += _ =>
        {
            Log.Info($"limit {Limit}");
            if (s_manager != null && s_manager.TryGetTarget(out ActiveActionManager? manager)) Trim(manager, Limit, force: false);
        };
    }

    public override void Load() => Harmony.PatchAll(typeof(MultitaskMod).Assembly);

    public override void Ready() => OnScene("res://Entities/OfflineProgress/offline_snapshot_modal.tscn", FitSummary);

    private const string SummaryRow = "MainContainer/Panel/MarginContainer/MainContainer/ScrollContainer";

    private void FitSummary(Node modal)
    {
        if (modal.GetNodeOrNull(SummaryRow) is not ScrollContainer scroll || scroll.GetChildOrNull<Control>(0) is not { } row)
        {
            Log.Warn("the time away summary is not laid out as expected; left alone");
            return;
        }
        scroll.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        void Fit()
        {
            float visible = scroll.GetTree().Root.GetVisibleRect().Size.X - 64;
            scroll.CustomMinimumSize = new Vector2(Math.Min(row.GetCombinedMinimumSize().X, visible), scroll.CustomMinimumSize.Y);
        }
        // the panels are added after the scene is in the tree
        row.MinimumSizeChanged += Fit;
        Window root = scroll.GetTree().Root;
        root.SizeChanged += Fit;
        scroll.TreeExiting += () => root.SizeChanged -= Fit;
        Fit();
    }

    // Stops the longest-running actions until at most `keep` are left. An action that
    // refuses to stop is passed over. Says whether it got there.
    private static bool Trim(ActiveActionManager manager, int keep, bool force)
    {
        foreach (ActionComponent action in manager.ActiveActions.ToList())
        {
            if (manager.ActiveActions.Count <= keep) break;
            action.TryStop(force);
        }
        return manager.ActiveActions.Count <= keep;
    }

    [HarmonyPatch(typeof(ActiveActionManager), nameof(ActiveActionManager.TryStopActive))]
    private static class TryStopActivePatch
    {
        private static bool Prefix(ActiveActionManager __instance, bool force, ref bool __result)
        {
            s_manager = new WeakReference<ActiveActionManager>(__instance);
            if (Limit <= 1) return true;
            // room for the one about to start
            __result = Trim(__instance, Limit - 1, force);
            return false;
        }
    }

    [HarmonyPatch(typeof(ActiveActionManager), nameof(ActiveActionManager.OnSaveLoaded))]
    private static class OnSaveLoadedPatch
    {
        private static void Postfix(ActiveActionManager __instance) => s_manager = new WeakReference<ActiveActionManager>(__instance);
    }

    private static Game? GameOf(object view) => Traverse.Create(view).Property("Game").GetValue<Game>();

    [HarmonyPatch(typeof(GameSidebarLegacy), nameof(GameSidebarLegacy.Update))]
    private static class SidebarUpdatePatch
    {
        private static readonly MethodInfo s_bind = AccessTools.Method(typeof(GameSidebarLegacy), "UpdateForActiveAction");

        private static void Postfix(GameSidebarLegacy __instance)
        {
            if (GameOf(__instance) is not { } game) return;
            foreach (ActionComponent action in game.ActionManager.ActiveActions.Skip(1))
                s_bind.Invoke(__instance, new object[] { action });
        }
    }

    private static IGameEntity? s_viewed;           // the entity of the page being looked at
    private static ActionComponent? s_shown;        // what the footer shows
    private static WeakReference<GameFooter>? s_footer;

    [HarmonyPatch(typeof(GameFooter), nameof(GameFooter.Update))]
    private static class FooterUpdatePatch
    {
        private static readonly MethodInfo s_bind = AccessTools.Method(typeof(GameFooter), "BindAction");

        private static bool Prefix(GameFooter __instance)
        {
            s_footer = new WeakReference<GameFooter>(__instance);
            if (GameOf(__instance) is not { } game) return true;
            var actions = game.ActionManager.ActiveActions;
            if (actions.Count < 2) return true;
            ActionComponent pick = actions.FirstOrDefault(a => ReferenceEquals(a.Entity, s_viewed))
                ?? (s_shown != null && actions.Contains(s_shown) ? s_shown : actions[actions.Count - 1]);
            s_shown = pick;
            s_bind.Invoke(__instance, new object[] { pick });
            return false;
        }
    }

    [HarmonyPatch(typeof(GameView), nameof(GameView.SetCurrentPage))]
    private static class SetCurrentPagePatch
    {
        private static void Postfix(GamePage pageData)
        {
            s_viewed = (pageData as EntityPage)?.Entity;
            if (s_footer != null && s_footer.TryGetTarget(out GameFooter? footer) && GodotObject.IsInstanceValid(footer)) footer.Update();
        }
    }
}
