using System;
using System.Collections.Generic;
using System.Reflection;
using AmongUs.GameOptions;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace SuperImpostor
{
    [BepInPlugin(Id, "SuperImpostor", "4.0.0")]
    public class SuperImpostorPlugin : BasePlugin
    {
        public const string Id = "com.victorgame195929.superimpostor";

        public static ManualLogSource Logger;
        public static bool NoKillCooldown = true;   // F2
        public static bool ViperKill = true;        // F3: fallback de dissolver o corpo quando voce nao for Viper
        public static bool Invisible = false;
        public static bool Shifted = false;

        public override void Load()
        {
            Logger = Log;
            var harmony = new Harmony(Id);
            harmony.PatchAll();

            // Patches feitos a mao: se algum nome mudar na sua versao do jogo, so aquela parte deixa de funcionar.
            try
            {
                var m = AccessTools.Method(typeof(PlayerControl), "MurderPlayer");
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(MurderPatch), nameof(MurderPatch.Postfix)));
            }
            catch (Exception e) { Log.LogWarning("Patch do kill nao aplicado: " + e.Message); }

            try
            {
                var ga = AccessTools.TypeByName("GridArrange");
                var m = ga != null ? AccessTools.Method(ga, "ArrangeChilds") : null;
                if (m != null) harmony.Patch(m, postfix: new HarmonyMethod(typeof(GridPatch), nameof(GridPatch.Postfix)));
            }
            catch (Exception e) { Log.LogWarning("Patch do layout nao aplicado: " + e.Message); }

            Log.LogInfo("SuperImpostor 4.0 carregado. Teclas: F1 vira Impostor | G Disfarce | V Sumir/Aparecer | B dissolver corpo | K matar | F2 cooldown | F3 fallback dissolver | F9 diagnostico");
        }
    }

    public static class Util
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static object GetMember(object o, string name)
        {
            if (o == null) return null;
            try
            {
                var t = o.GetType();
                var p = t.GetProperty(name, Flags);
                if (p != null) return p.GetValue(o);
                var f = t.GetField(name, Flags);
                return f?.GetValue(o);
            }
            catch { return null; }
        }

        public static bool TryInvoke(object obj, string name, params object[] args)
        {
            if (obj == null) return false;
            foreach (var m in obj.GetType().GetMethods(Flags))
            {
                if (m.Name != name || m.GetParameters().Length != args.Length) continue;
                try { m.Invoke(obj, args); return true; }
                catch (Exception e)
                {
                    SuperImpostorPlugin.Logger.LogWarning($"Falha em {obj.GetType().Name}.{name}: {e.InnerException?.Message ?? e.Message}");
                }
            }
            return false;
        }

        public static PlayerControl NearestPlayer(PlayerControl me)
        {
            PlayerControl target = null;
            float best = float.MaxValue;
            foreach (var p in PlayerControl.AllPlayerControls)
            {
                if (p == null || p == me || p.Data == null) continue;
                if (p.Data.IsDead || p.Data.Disconnected) continue;
                float d = Vector2.Distance(me.GetTruePosition(), p.GetTruePosition());
                if (d < best) { best = d; target = p; }
            }
            return target;
        }

        public static DeadBody NearestBody(PlayerControl me)
        {
            DeadBody nearest = null;
            float best = float.MaxValue;
            foreach (var b in UnityEngine.Object.FindObjectsOfType<DeadBody>())
            {
                float d = Vector2.Distance(me.GetTruePosition(), b.transform.position);
                if (d < best) { best = d; nearest = b; }
            }
            return nearest;
        }

        public static void DissolveBody(DeadBody b)
        {
            if (b == null) return;
            foreach (var n in new[] { "Dissolve", "StartDissolve", "DissolveBody" })
                if (TryInvoke(b, n)) return;
            SuperImpostorPlugin.Logger.LogWarning("Nenhum metodo de dissolver em DeadBody; removendo o corpo so no seu lado. Use F9 para ver os nomes.");
            UnityEngine.Object.Destroy(b.gameObject);
        }

        public static Sprite RoleIcon(string roleName)
        {
            try
            {
                var rm = RoleManager.Instance;
                if (rm == null) return null;
                foreach (var r in rm.AllRoles)
                {
                    if (r == null || r.Role.ToString() != roleName) continue;
                    return GetMember(GetMember(r, "Ability"), "Image") as Sprite;
                }
            }
            catch { }
            return null;
        }

        public static void Dump()
        {
            string[] keys = { "Vanish", "Appear", "Invis", "Dissolve", "Shapeshift", "Phantom", "Viper", "UseAbility" };
            string[] types = { "PlayerControl", "PhantomRole", "ViperRole", "ShapeshifterRole", "DeadBody", "RoleBehaviour" };
            foreach (var t in typeof(PlayerControl).Assembly.GetTypes())
            {
                if (Array.IndexOf(types, t.Name) < 0) continue;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    foreach (var k in keys)
                    {
                        if (m.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var ps = string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name));
                            SuperImpostorPlugin.Logger.LogInfo($"[dump] {t.Name}.{m.Name}({ps})");
                            break;
                        }
                    }
                }
            }
        }
    }

    public static class Actions
    {
        static readonly List<KeyValuePair<float, Action>> Later = new List<KeyValuePair<float, Action>>();
        static float lastSwap;
        static float holdUntil;     // nao volta para Viper antes disso (menu/efeito em andamento)
        static float shipSeen = -1f;
        static int homeTries;

        // So o HOST pode trocar de funcao. Clientes que mandam RpcSetRole sao tratados como trapaceiros e
        // expulsos da partida, por isso nenhuma troca e feita quando voce nao e o host.
        public static bool IsHost()
        {
            return AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        }

        public static void After(float seconds, Action a)
        {
            Later.Add(new KeyValuePair<float, Action>(Time.time + seconds, a));
        }

        public static void Tick()
        {
            for (int i = Later.Count - 1; i >= 0; i--)
            {
                if (Time.time < Later[i].Key) continue;
                var a = Later[i].Value;
                Later.RemoveAt(i);
                try { a(); } catch (Exception e) { SuperImpostorPlugin.Logger.LogWarning("Erro em acao agendada: " + e.Message); }
            }
        }

        public static string RoleName(PlayerControl me)
        {
            return me != null && me.Data != null && me.Data.Role != null ? me.Data.Role.Role.ToString() : "";
        }

        static void SetRoleByName(PlayerControl me, string roleName)
        {
            me.RpcSetRole((RoleTypes)Enum.Parse(typeof(RoleTypes), roleName));
            lastSwap = Time.time;
        }

        // Usa a habilidade NATIVA da funcao atual (Metamorfo abre o menu de jogadores; Phantom alterna invisibilidade).
        static void UseNative(PlayerControl me)
        {
            if (!Util.TryInvoke(me.Data.Role, "UseAbility"))
                SuperImpostorPlugin.Logger.LogWarning("UseAbility nao encontrado na funcao " + RoleName(me) + ". Aperte F9 para listar.");
        }

        static void WithRole(PlayerControl me, string roleName, Action act)
        {
            if (RoleName(me) == roleName) { act(); return; }
            if (!IsHost())
            {
                SuperImpostorPlugin.Logger.LogInfo("So o host pode trocar de funcao; voce e " + RoleName(me) + ", entao " + roleName + " nao esta disponivel.");
                return;
            }
            homeTries = 0;
            SetRoleByName(me, roleName);
            After(0.7f, act);
        }

        public static void UseShapeshift()
        {
            var me = PlayerControl.LocalPlayer;
            if (me == null || me.Data == null || me.Data.IsDead) return;
            WithRole(me, "Shapeshifter", () =>
            {
                var m = PlayerControl.LocalPlayer;
                if (m == null) return;
                holdUntil = Time.time + 25f;
                UseNative(m); // abre o menu de selecao de jogador (ou desfaz se ja estiver transformado)
            });
        }

        public static void UseVanish()
        {
            var me = PlayerControl.LocalPlayer;
            if (me == null || me.Data == null || me.Data.IsDead) return;
            WithRole(me, "Phantom", () =>
            {
                var m = PlayerControl.LocalPlayer;
                if (m == null) return;
                holdUntil = Time.time + 10f;
                UseNative(m);
                SuperImpostorPlugin.Invisible = !SuperImpostorPlugin.Invisible;
            });
        }

        // Le o estado real quando o jogo expoe (para o texto dos botoes e para saber quando voltar a Viper).
        public static void SyncStates(PlayerControl me)
        {
            if (me == null || me.Data == null || me.Data.Role == null) return;
            string r = RoleName(me);
            if (r == "Phantom")
            {
                var v = Util.GetMember(me.Data.Role, "IsInvisible");
                if (v is bool) SuperImpostorPlugin.Invisible = (bool)v;
            }
            else if (SuperImpostorPlugin.Invisible && Time.time > holdUntil) SuperImpostorPlugin.Invisible = false;

            foreach (var n in new[] { "IsShapeshifted", "shapeshifting", "Shapeshifting" })
            {
                var v = Util.GetMember(me, n);
                if (v is bool) { SuperImpostorPlugin.Shifted = (bool)v; break; }
            }
        }

        // Funcao base = Viper: e o proprio jogo que dissolve o corpo (com animacao) quando o Viper mata.
        public static void EnsureHomeRole(PlayerControl me)
        {
            if (ShipStatus.Instance == null) { shipSeen = -1f; return; }
            if (shipSeen < 0f) shipSeen = Time.time;

            if (me == null || me.Data == null || me.Data.Role == null || !me.Data.Role.IsImpostor || me.Data.IsDead) return;
            if (MeetingHud.Instance != null) return;
            if (RoleName(me) == "Viper") { homeTries = 0; return; }

            if (!IsHost()) return;                                   // cliente nunca troca de funcao
            if (Time.time - shipSeen < 10f) return;                  // espera a partida comecar de verdade
            if (Time.time < holdUntil) return;                       // menu/efeito em andamento
            if (SuperImpostorPlugin.Shifted || SuperImpostorPlugin.Invisible) return;
            if (Later.Count > 0) return;
            if (Time.time - lastSwap < 3f || homeTries >= 3) return;

            homeTries++;
            try
            {
                SetRoleByName(me, "Viper");
                SuperImpostorPlugin.Logger.LogInfo("Funcao base: Viper.");
            }
            catch (Exception e)
            {
                homeTries = 99;
                SuperImpostorPlugin.Logger.LogWarning("Nao consegui voltar para Viper: " + e.Message);
            }
        }

        public static void BecomeImpostor()
        {
            var me = PlayerControl.LocalPlayer;
            if (me == null) return;
            if (!IsHost()) { SuperImpostorPlugin.Logger.LogInfo("So o host pode mudar de funcao."); return; }
            SetRoleByName(me, "Impostor");
            SuperImpostorPlugin.Logger.LogInfo("Voce agora e Impostor.");
        }

        public static void DissolveNearest()
        {
            var me = PlayerControl.LocalPlayer;
            if (me == null) return;
            var b = Util.NearestBody(me);
            if (b == null) { SuperImpostorPlugin.Logger.LogInfo("Nenhum corpo no mapa."); return; }
            Util.DissolveBody(b);
        }

        public static void KillNearest()
        {
            var me = PlayerControl.LocalPlayer;
            if (me == null || me.Data == null || me.Data.IsDead) return;
            var t = Util.NearestPlayer(me);
            if (t != null) me.CmdCheckMurder(t);
        }
    }

    // Fallback: se voce NAO for Viper, dissolve o corpo da vitima por conta propria (sem a animacao nativa).
    public static class MurderPatch
    {
        public class Pending { public byte Id; public float Start; public float End; }
        public static readonly List<Pending> Queue = new List<Pending>();

        public static void Postfix(PlayerControl __instance, PlayerControl target)
        {
            if (!SuperImpostorPlugin.ViperKill) return;
            if (__instance == null || target == null || !__instance.AmOwner) return;
            if (Actions.RoleName(__instance) == "Viper") return; // Viper: o jogo ja dissolve com animacao
            Queue.Add(new Pending { Id = target.PlayerId, Start = Time.time + 0.5f, End = Time.time + 4f });
        }

        public static void Process()
        {
            for (int i = Queue.Count - 1; i >= 0; i--)
            {
                var p = Queue[i];
                if (Time.time < p.Start) continue;
                DeadBody found = null;
                foreach (var b in UnityEngine.Object.FindObjectsOfType<DeadBody>())
                    if (b.ParentId == p.Id) { found = b; break; }
                if (found != null) { Util.DissolveBody(found); Queue.RemoveAt(i); }
                else if (Time.time > p.End) Queue.RemoveAt(i);
            }
        }
    }

    // Reaplica o layout logo depois que o jogo reorganiza a grade de botoes.
    public static class GridPatch
    {
        public static void Postfix() { Buttons.RelayoutNow(); }
    }

    public class CustomButton
    {
        public ActionButton Btn;
        public Action Click;
        public Func<string> Label;
        public string LastLabel;
        public string Name;
        public int Slot;   // 0 = colado a esquerda do Acido, 1 = mais uma casa a esquerda
    }

    public static class Buttons
    {
        // Layout (ajuste aqui se ficar apertado):
        //   [ Sumir ][ Disfarce ][ Acido (kill) ]      <- mesma linha do kill
        //                        [ Duto ]              <- uma linha acima do kill
        public const float ColSpacing = 1.0f;
        public const float RowSpacing = 1.05f;

        static readonly List<CustomButton> List = new List<CustomButton>();
        static Sprite acidIcon;
        static int frame;
        static bool lastInGame;

        static void Make(HudManager hud, string name, Sprite icon, int slot, Action click, Func<string> label)
        {
            var proto = hud.AbilityButton;
            var inst = UnityEngine.Object.Instantiate(proto, proto.transform.parent);
            ActionButton btn = inst;
            btn.gameObject.name = "SuperImpostor_" + name;

            var ap = btn.GetComponent<AspectPosition>();
            if (ap != null) UnityEngine.Object.Destroy(ap);

            if (icon != null && btn.graphic != null) btn.graphic.sprite = icon;

            // Desliga o clique nativo do clone (senao dispararia a habilidade da sua funcao).
            var pb = btn.GetComponent<PassiveButton>();
            if (pb != null)
            {
                pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
                pb.enabled = false;
            }

            List.Add(new CustomButton { Btn = btn, Click = click, Label = label, Name = name, Slot = slot });
        }

        static void Ensure(HudManager hud)
        {
            if (List.Count > 0 && List[0].Btn == null) List.Clear(); // HUD recriado (nova partida)
            if (List.Count > 0 || hud.AbilityButton == null) return;

            Make(hud, "Disfarce", Util.RoleIcon("Shapeshifter"), 0, Actions.UseShapeshift,
                () => SuperImpostorPlugin.Shifted ? "Desfazer" : "Disfarce");
            Make(hud, "Sumir", Util.RoleIcon("Phantom"), 1, Actions.UseVanish,
                () => SuperImpostorPlugin.Invisible ? "Aparecer" : "Sumir");
        }

        // Sendo Viper, o kill nativo ja e o botao de Acido (sprite do Viper). Guardamos esse sprite e,
        // nas outras funcoes (Metamorfo/Phantom temporarios), o kill continua com o visual de Acido.
        static void KillVisual(HudManager hud, bool viper)
        {
            var kb = hud.KillButton;
            if (kb == null) return;
            if (viper)
            {
                if (kb.graphic != null && kb.graphic.sprite != null) acidIcon = kb.graphic.sprite;
                return;
            }
            if (acidIcon == null && frame % 60 == 0) acidIcon = Util.RoleIcon("Viper");
            if (acidIcon != null && kb.graphic != null && kb.graphic.sprite != acidIcon) kb.graphic.sprite = acidIcon;
            if (frame % 30 == 0) kb.OverrideText("Ácido");
        }

        static void HideBadge(ActionButton btn)
        {
            var spr = Util.GetMember(btn, "usesRemainingSprite") as Component;
            if (spr != null) spr.gameObject.SetActive(false);
            var txt = Util.GetMember(btn, "usesRemainingText") as Component;
            if (txt != null) txt.gameObject.SetActive(false);
        }

        static void Layout(HudManager hud)
        {
            var kb = hud.KillButton;
            if (kb == null) return;
            var kp = kb.transform.localPosition;

            // Duto em cima do Acido
            var vent = Util.GetMember(hud, "ImpostorVentButton") as Component;
            if (vent != null && vent.gameObject.activeSelf)
                vent.transform.localPosition = new Vector3(kp.x, kp.y + RowSpacing, kp.z);

            // Disfarce e Sumir ao lado do Acido
            foreach (var cb in List)
            {
                if (cb.Btn == null) continue;
                cb.Btn.transform.localPosition = new Vector3(kp.x - ColSpacing * (cb.Slot + 1), kp.y, kp.z);
            }
        }

        public static void RelayoutNow()
        {
            var hud = HudManager.Instance;
            if (hud == null || !lastInGame) return;
            Layout(hud);
        }

        public static void Update(HudManager hud, PlayerControl me)
        {
            frame++;
            Ensure(hud);

            bool isImpostor = me != null && me.Data != null && me.Data.Role != null && me.Data.Role.IsImpostor;
            bool inGame = isImpostor && ShipStatus.Instance != null && MeetingHud.Instance == null && !me.Data.IsDead;
            lastInGame = inGame;

            // Esconde o botao nativo de habilidade (Anula/Desaparecer...): os nossos botoes o substituem, sem duplicar.
            if (inGame && hud.AbilityButton != null) hud.AbilityButton.gameObject.SetActive(false);

            if (inGame) KillVisual(hud, Actions.RoleName(me) == "Viper");
            if (inGame) Layout(hud);

            // Clique / toque
            bool clicked = false;
            Vector2 clickPos = default(Vector2);
            if (Input.GetMouseButtonDown(0)) { clicked = true; clickPos = Input.mousePosition; }
            else if (Input.touchCount > 0)
            {
                var t = Input.GetTouch(0);
                if (t.phase == TouchPhase.Began) { clicked = true; clickPos = t.position; }
            }
            Camera cam = (Util.GetMember(hud, "UICamera") as Camera) ?? Camera.main;

            foreach (var cb in List)
            {
                if (cb.Btn == null) continue;
                cb.Btn.gameObject.SetActive(inGame);
                if (!inGame) continue;

                cb.Btn.SetCoolDown(0f, 1f);
                cb.Btn.SetEnabled();
                HideBadge(cb.Btn);

                var label = cb.Label();
                if (label != cb.LastLabel)
                {
                    cb.Btn.OverrideText(label);
                    cb.LastLabel = label;
                }

                if (clicked && cam != null && cb.Btn.graphic != null)
                {
                    var w = cam.ScreenToWorldPoint(new Vector3(clickPos.x, clickPos.y, 0f));
                    var bb = cb.Btn.graphic.bounds;
                    if (w.x >= bb.min.x && w.x <= bb.max.x && w.y >= bb.min.y && w.y <= bb.max.y)
                    {
                        SuperImpostorPlugin.Logger.LogInfo("Botao clicado: " + cb.Name);
                        cb.Click();
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
    public static class HudManagerUpdatePatch
    {
        public static void Postfix(HudManager __instance)
        {
            var me = PlayerControl.LocalPlayer;
            if (me == null || me.Data == null) return;

            Buttons.Update(__instance, me);
            MurderPatch.Process();
            Actions.Tick();
            Actions.SyncStates(me);
            Actions.EnsureHomeRole(me);

            if (Input.GetKeyDown(KeyCode.F1)) Actions.BecomeImpostor();
            if (Input.GetKeyDown(KeyCode.F2))
            {
                SuperImpostorPlugin.NoKillCooldown = !SuperImpostorPlugin.NoKillCooldown;
                SuperImpostorPlugin.Logger.LogInfo("Cooldown zero: " + SuperImpostorPlugin.NoKillCooldown);
            }
            if (Input.GetKeyDown(KeyCode.F3))
            {
                SuperImpostorPlugin.ViperKill = !SuperImpostorPlugin.ViperKill;
                SuperImpostorPlugin.Logger.LogInfo("Fallback de dissolver: " + SuperImpostorPlugin.ViperKill);
            }
            if (Input.GetKeyDown(KeyCode.F9)) Util.Dump();

            if (me.Data.IsDead) return;

            if (SuperImpostorPlugin.NoKillCooldown && me.Data.Role != null && me.Data.Role.IsImpostor)
                me.SetKillTimer(0f);

            if (Input.GetKeyDown(KeyCode.K)) Actions.KillNearest();
            if (Input.GetKeyDown(KeyCode.G)) Actions.UseShapeshift();
            if (Input.GetKeyDown(KeyCode.V)) Actions.UseVanish();
            if (Input.GetKeyDown(KeyCode.B)) Actions.DissolveNearest();
        }
    }
}
