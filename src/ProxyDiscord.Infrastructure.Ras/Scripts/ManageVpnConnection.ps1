param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Create', 'Remove')]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [string]$Name,

    [string]$ServerAddress,

    [ValidateSet('Sstp')]
    [string]$TunnelType
)

$ErrorActionPreference = 'Stop'

function Remove-EntryEverywhere {
    param([string]$EntryName)
    # Both scopes are attempted: the app creates user-scope entries (see below), but a build
    # from before that change may have left an all-user entry behind.
    Remove-VpnConnection -Name $EntryName -Force -ErrorAction SilentlyContinue
    Remove-VpnConnection -Name $EntryName -AllUserConnection -Force -ErrorAction SilentlyContinue
}

switch ($Action) {
    'Create' {
        Remove-EntryEverywhere -EntryName $Name

        $connectionParams = @{
            Name                 = $Name
            ServerAddress        = $ServerAddress
            TunnelType           = $TunnelType
            EncryptionLevel      = 'Optional'
            AuthenticationMethod = 'MSChapv2'
            RememberCredential   = $true
            # Critical for this app: without split tunneling Windows makes the VPN the default
            # gateway and the WHOLE machine egresses through it. This app routes only the one
            # selected process, so the system default route must stay untouched.
            #
            # The consequence is that the VPN interface gets NO route at all, and a socket pinned
            # to it with IP_UNICAST_IF then has nothing to resolve against - which is exactly how
            # this app used to report "connected" while tunnelling nothing. VpnRouteManager adds a
            # deliberately high-metric default route on the interface to close that gap.
            SplitTunneling       = $true
            Force                = $true
        }

        # Deliberately user-scope (no -AllUserConnection): rasdial.exe resolves entry names
        # against the per-user phonebook by default, and an all-user entry is not reliably
        # found there. Verified empirically — a user-scope entry dials, and a missing entry
        # is the only case that yields RAS error 623.
        Add-VpnConnection @connectionParams | Out-Null
    }
    'Remove' {
        Remove-EntryEverywhere -EntryName $Name
    }
}
