using McSH;
using McSH.Services;
using Spectre.Console;

namespace McSH.Commands;

public class HelpCommand
{
    private static string L(string key) => LanguageService.Get(key);

    // Quick-start guide shown by 'help'
    public void Execute()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Command[/]").NoWrap())
            .AddColumn("[bold]Description[/]");

        table.AddRow($"[{UiTheme.AccentMarkup}]auth login[/]",                        L("help.cmd_auth_login"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance create[/]",                   L("help.cmd_instance_create"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance select [grey]<name>[/][/]",   L("help.cmd_instance_select"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance run[/] [dim]/ launch[/]",      L("help.cmd_instance_run"));
        table.AddRow($"[{UiTheme.AccentMarkup}]modpack search [grey]<query>[/][/]",   L("help.cmd_modpack_search"));
        table.AddRow($"[{UiTheme.AccentMarkup}]mod search [grey]<query>[/][/]",       L("help.cmd_mod_search"));
        table.AddRow($"[{UiTheme.AccentMarkup}]settings[/]",                          L("help.cmd_settings"));
        table.AddRow($"[{UiTheme.AccentMarkup}]exit[/]",                              L("help.cmd_exit"));
        table.AddEmptyRow();
        table.AddRow("[dim]ref[/]", $"[dim]{L("help.cmd_ref")}[/]");

        AnsiConsole.Write(table);
    }

    // Full command reference shown by 'ref' / 'c'
    public void ExecuteRef()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Command[/]").NoWrap())
            .AddColumn("[bold]Description[/]");

        table.AddRow($"[{UiTheme.AccentMarkup}]instance list[/]",                          L("help.ref_instance_list"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance create[/]",                        L("help.ref_instance_create"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance select [grey]<name>[/][/]",        L("help.ref_instance_select"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance deselect[/]",                      L("help.ref_instance_deselect"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance run [grey][[name]][/][/]",             L("help.ref_instance_run"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance clone [grey]<name>[/][/]",          L("help.ref_instance_clone"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance stop[/] [dim]/ kill[/] [grey][[name]][/]", L("help.ref_instance_kill"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance delete [grey]<name>[/][/]",        L("help.ref_instance_delete"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance rename [grey]<old> <new>[/][/]",   L("help.ref_instance_rename"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance update [grey][[name]][/][/]",       L("help.ref_instance_update"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance worlds [grey][[name]][/][/]",       L("help.ref_instance_worlds"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance screenshots [grey][[name]][/][/]",  L("help.ref_instance_screenshots"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance crash [grey][[name]][/][/]",        L("help.ref_instance_crash"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance import [grey]<path.mrpack>[/][/]", L("help.ref_instance_import"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance export [grey][[name]][/][/]",       L("help.ref_instance_export"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance backup [grey][[name]][/][/]",       L("help.ref_instance_backup"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance prism [grey]<path>[/][/]",          L("help.ref_instance_prism"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance mrpack [grey][[name]] [[ver]][/][/]", L("help.ref_instance_mrpack"));
        table.AddRow($"[{UiTheme.AccentMarkup}]instance config [grey][[name]][/][/]",         L("help.ref_instance_config"));
        table.AddEmptyRow();
        table.AddRow($"[{UiTheme.AccentMarkup}]modpack search [grey]<query>[/][/]",  L("help.ref_modpack_search"));
        table.AddRow($"[{UiTheme.AccentMarkup}]modpack install [grey]<#|slug>[/][/]", L("help.ref_modpack_install"));
        table.AddRow($"[{UiTheme.AccentMarkup}]modpack update[/]",                   L("help.ref_modpack_update"));
        table.AddEmptyRow();
        table.AddRow($"[{UiTheme.AccentMarkup}]mod search [grey]<query>[/][/]",   L("help.ref_mod_search"));
        table.AddRow($"[{UiTheme.AccentMarkup}]mod install [grey]<id>[/][/]",     L("help.ref_mod_install"));
        table.AddRow($"[{UiTheme.AccentMarkup}]mod remove[/] [dim]/ uninstall[/] [grey]<name>[/]", L("help.ref_mod_remove"));
        table.AddRow($"[{UiTheme.AccentMarkup}]mod details [grey]<id|#>[/][/]",   L("help.ref_mod_details"));
        table.AddRow($"[{UiTheme.AccentMarkup}]mod import [grey]<path>[/][/]",    L("help.ref_mod_import"));
        table.AddRow($"[{UiTheme.AccentMarkup}]mod toggle [grey]<name>[/][/]",    L("help.ref_mod_toggle"));
        table.AddRow($"[{UiTheme.AccentMarkup}]mod open[/]",                      L("help.ref_mod_open"));
        table.AddRow($"[{UiTheme.AccentMarkup}]mod list[/]",                      L("help.ref_mod_list"));
        table.AddRow($"[{UiTheme.AccentMarkup}]mod profile[/] [grey]save|load|list|delete[/]", L("help.ref_mod_profile"));
        table.AddEmptyRow();
        table.AddRow($"[{UiTheme.AccentMarkup}]resourcepack search[/] [grey]<query>[/]", L("help.ref_rp_search"));
        table.AddRow($"[{UiTheme.AccentMarkup}]resourcepack install[/] [grey]<#>[/]",    L("help.ref_rp_install"));
        table.AddRow($"[{UiTheme.AccentMarkup}]resourcepack details[/] [grey]<#>[/]",    L("help.ref_rp_details"));
        table.AddRow($"[{UiTheme.AccentMarkup}]shader search[/] [grey]<query>[/]",       L("help.ref_shader_search"));
        table.AddRow($"[{UiTheme.AccentMarkup}]shader install[/] [grey]<#>[/]",          L("help.ref_shader_install"));
        table.AddRow($"[{UiTheme.AccentMarkup}]shader details[/] [grey]<#>[/]",          L("help.ref_shader_details"));
        table.AddRow($"[{UiTheme.AccentMarkup}]plugin search[/] [grey]<query>[/]",       L("help.ref_plugin_search"));
        table.AddRow($"[{UiTheme.AccentMarkup}]plugin install[/] [grey]<#>[/]",          L("help.ref_plugin_install"));
        table.AddRow($"[{UiTheme.AccentMarkup}]plugin details[/] [grey]<#>[/]",          L("help.ref_plugin_details"));
        table.AddRow($"[{UiTheme.AccentMarkup}]datapack search[/] [grey]<query>[/]",     L("help.ref_datapack_search"));
        table.AddRow($"[{UiTheme.AccentMarkup}]datapack install[/] [grey]<#>[/]",        L("help.ref_datapack_install"));
        table.AddRow($"[{UiTheme.AccentMarkup}]datapack details[/] [grey]<#>[/]",        L("help.ref_datapack_details"));
        table.AddEmptyRow();
        table.AddRow($"[{UiTheme.AccentMarkup}]java[/]",                                  L("help.ref_java"));
        table.AddRow($"[{UiTheme.AccentMarkup}]skin[/]",                                 L("help.ref_skin"));
        table.AddRow($"[{UiTheme.AccentMarkup}]skin import [grey]<path.png>[/][/]",      L("help.ref_skin_import"));
        table.AddRow($"[{UiTheme.AccentMarkup}]skin cape[/]",                            L("help.ref_skin_cape"));
        table.AddRow($"[{UiTheme.AccentMarkup}]skin delete [grey]<name>[/][/]",          L("help.ref_skin_delete"));
        table.AddRow($"[{UiTheme.AccentMarkup}]console[/]",                              L("help.ref_console"));
        table.AddRow($"[{UiTheme.AccentMarkup}]auth login [grey][[alias]][/][/]",         L("help.ref_auth_login"));
        table.AddRow($"[{UiTheme.AccentMarkup}]auth accounts[/]",                         L("help.ref_auth_accounts"));
        table.AddRow($"[{UiTheme.AccentMarkup}]auth switch [grey]<alias>[/][/]",          L("help.ref_auth_switch"));
        table.AddRow($"[{UiTheme.AccentMarkup}]auth remove [grey]<alias>[/][/]",          L("help.ref_auth_remove"));
        table.AddRow($"[{UiTheme.AccentMarkup}]auth logout[/]",                           L("help.ref_auth_logout"));
        table.AddRow($"[{UiTheme.AccentMarkup}]settings[/]",                              L("help.ref_settings"));
        table.AddRow($"[{UiTheme.AccentMarkup}]settings theme[/] [grey][[name]][/]",          L("help.ref_settings_theme"));
        table.AddRow($"[{UiTheme.AccentMarkup}]settings language [grey]<en|es>[/][/]",     L("help.ref_settings_language"));
        table.AddRow($"[{UiTheme.AccentMarkup}]settings recent [grey]<count>[/][/]",       L("help.ref_settings_recent"));
        table.AddEmptyRow();
        table.AddRow($"[{UiTheme.AccentMarkup}]recent[/]",                                L("help.ref_recent"));
        table.AddRow($"[{UiTheme.AccentMarkup}]recent run [grey]<#>[/][/]",               L("help.ref_recent_run"));
        table.AddRow($"[{UiTheme.AccentMarkup}]help[/]",                                  L("help.ref_help"));
        table.AddRow($"[{UiTheme.AccentMarkup}]ref[/]",                                   L("help.ref_ref"));
        table.AddRow($"[{UiTheme.AccentMarkup}]version[/]",                               L("help.ref_version"));
        table.AddRow($"[{UiTheme.AccentMarkup}]update[/]",                                L("help.ref_update"));
        table.AddRow($"[{UiTheme.AccentMarkup}]restart[/]",                               L("help.ref_restart"));
        table.AddRow($"[{UiTheme.AccentMarkup}]clear[/]",                                 L("help.ref_clear"));
        table.AddRow($"[{UiTheme.AccentMarkup}]exit[/]",                                  L("help.ref_exit"));

        AnsiConsole.Write(table);
    }
}
