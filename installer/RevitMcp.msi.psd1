# AB Revit MCP Bridge - what its .msi installs and where. Built by build\build-installer.ps1 through
# the AB Adv Tools kit (shared\ABAdvTools\msi\New-AdvToolsMsi.ps1); the kit README documents every key.
@{
    # Keys the installer's registry state and finds copies left by the retired Setup.exe. Never rename.
    Id          = 'RevitMcp'
    Name        = 'AB Revit MCP Bridge'
    FileName    = 'AB.RevitMcp'
    Repository  = 'AB.RevitMcp'

    # First .msi release (1.5.0). Never change it.
    UpgradeCode = '3E9B6C21-7A4D-4F58-B1E3-9C2D5A8F0B64'

    Description = 'Lets any MCP-compatible AI client - Claude, Cursor, VS Code, a local model - safely query and edit the open Revit model. Installs the Revit add-in and the MCP server for you.'
    NextSteps   = 'Start Revit, open the AB Adv Tools tab and press Start Bridge on the MCP Bridge panel. The first time Revit starts it configures the AI clients you ticked; restart those clients afterwards. AI Clients on the same panel adds a custom client, reconfigures, or verifies the setup at any time.'
    RemovedText = 'AB Revit MCP Bridge has been removed. Settings and logs are kept. Remove the "revit" entry from your AI client configuration if you no longer want it.'
    Icon        = 'product.ico'

    # Per user only, as always: no administrator rights, nothing in Program Files. The add-in stays
    # out of %LOCALAPPDATA%: endpoint protection commonly blocks DLL loads from there.
    Scope = 'User'

    RevitAddin = @{
        ManifestFileName  = 'AB.RevitMcp.addin'
        FolderName        = 'ABRevitMcp'
        AssemblyFileName  = 'AB.RevitMcp.Addin.dll'
        AddInName         = 'AB MCP AI Bridge'
        FullClassName     = 'AB.RevitMcp.Addin.App'
        # Revit keys the registration off this id. Never change it.
        AddInId           = '7f3c9a12-5d84-4b1e-9c67-2a8e5f0d41b3'
        VendorId          = 'ABLOTFY'
        VendorDescription = 'Abdullah Lotfy - https://www.linkedin.com/in/abdullahalqublawi/'
        # Ticked for the Revit releases installed here, as Setup.exe offered.
        Releases          = 'Detected'
    }

    # The MCP server, where AI clients are pointed at: %LOCALAPPDATA%\ABRevitMcp\Server
    ExtraFolders = @(
        @{ Payload = 'Server'; Root = 'LocalAppData'; Path = 'ABRevitMcp\Server' }
    )

    # Discovery files the add-in writes; Setup.exe removed them on uninstall. Settings and logs stay.
    RemoveOnUninstall = @(
        @{ Root = 'LocalAppData'; Path = 'ABRevitMcp\endpoints' }
    )

    # The "AI clients" page and where its choice is kept (applied by the add-in in Revit).
    Fragments       = @('RevitMcp.AiClients.wxs')
    OptionDialogs   = @('ABAgentsDlg')
    ComponentGroups = @('AbAiClientChoices')
}
