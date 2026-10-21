<#
.SYNOPSIS
    One-time setup for saving AI results: creates the table (if missing) and stores the
    connection string in XENTRANS_DB for the PLC Logic Builder R&D Studio.

.DESCRIPTION
    Run it yourself - it asks for the database password, which is typed hidden and is
    never printed or written to any file. The password ends up only in the XENTRANS_DB
    user environment variable, the same way the API keys are stored.

    Steps:
      1. Ask for the password
      2. Test the connection
      3. Create XenCreator.dbo.OutputXenTrans if it does not exist (asks first)
      4. Save the connection string to XENTRANS_DB
    Then close and reopen the app.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File "D:\XenAnalyticTransl\Reference\sql\Setup-XenTransDb.ps1"
#>
param(
    [string]$Server   = '128.100.20.33\neptune',
    [string]$Database = 'XenCreator',
    [string]$User     = 'rndsa',
    # For testing only: Windows sign-in instead of a password, a different variable
    # name, and no prompts.
    [switch]$UseWindowsAuth,
    [string]$EnvVarName = 'XENTRANS_DB',
    [switch]$AssumeYes
)

$ErrorActionPreference = 'Stop'
$sqlFile = Join-Path $PSScriptRoot 'OutputXenTrans.sql'

function Write-Step($n, $text) { Write-Host "`n[$n/4] $text" -ForegroundColor Cyan }
function Write-Ok($text)       { Write-Host "      OK  $text" -ForegroundColor Green }
function Write-Bad($text)      { Write-Host "      !!  $text" -ForegroundColor Red }

# ---- 1. connection details ---------------------------------------------------
Write-Step 1 "Connection details"
$csb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$csb['Data Source']     = $Server
$csb['Initial Catalog'] = $Database
$csb['Connect Timeout'] = 15
$csb['TrustServerCertificate'] = $true

if ($UseWindowsAuth) {
    $csb['Integrated Security'] = $true
    Write-Host "      Server $Server, database $Database, Windows sign-in"
} else {
    Write-Host "      Server $Server, database $Database, login $User"
    $secure = Read-Host "      Password for $User (hidden)" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try   { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    if ([string]::IsNullOrEmpty($plain)) { Write-Bad "No password entered - nothing changed."; exit 1 }
    $csb['User ID']  = $User
    $csb['Password'] = $plain
    $plain = $null
}
$connectionString = $csb.ConnectionString

# ---- 2. test the connection ----------------------------------------------------
Write-Step 2 "Testing the connection"
try {
    $cn = New-Object System.Data.SqlClient.SqlConnection $connectionString
    $cn.Open()
    Write-Ok "Connected to $Server / $($cn.Database)"
} catch {
    # The message comes from SQL Server and never contains the password.
    $msg = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
    Write-Bad "Could not connect: $msg"
    Write-Bad "Nothing was changed. Check the password and that you are on the company network."
    exit 1
}

# ---- 3. the table ----------------------------------------------------------------
Write-Step 3 "Checking table dbo.OutputXenTrans"
try {
    $cmd = $cn.CreateCommand()
    $cmd.CommandText = "SELECT OBJECT_ID(N'dbo.OutputXenTrans', N'U')"
    $exists = -not [DBNull]::Value.Equals($cmd.ExecuteScalar())

    if ($exists) {
        Write-Ok "Table already exists - leaving it as it is"
    } else {
        Write-Host "      The table does not exist yet."
        $answer = if ($AssumeYes) { 'Y' } else { Read-Host "      Create it now from OutputXenTrans.sql? (Y/N)" }
        if ($answer -notmatch '^[Yy]') {
            Write-Bad "Table not created. Run OutputXenTrans.sql in SSMS, then run this script again."
            exit 1
        }
        # ADO.NET does not understand GO, so run the script one batch at a time. The
        # USE line is skipped: the connection already selected the database, and USE
        # would silently switch away from it if a different one was chosen.
        $script = Get-Content -Raw -Path $sqlFile
        $script = [regex]::Replace($script, '^\s*USE\s+[^\r\n]*$', '', 'Multiline')
        $batches = [regex]::Split($script, '^\s*GO\s*$', 'Multiline') | Where-Object { $_.Trim() }
        foreach ($b in $batches) {
            $cmd.CommandText = $b
            [void]$cmd.ExecuteNonQuery()
        }
        $cmd.CommandText = "SELECT OBJECT_ID(N'dbo.OutputXenTrans', N'U')"
        if ([DBNull]::Value.Equals($cmd.ExecuteScalar())) { throw "the script ran but the table is still missing" }
        Write-Ok "Table created"
    }
} catch {
    $msg = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
    Write-Bad "Could not create the table: $msg"
    Write-Bad "Your login may not have CREATE TABLE rights - ask the DBA to run OutputXenTrans.sql."
    exit 1
} finally {
    $cn.Close()
}

# ---- 4. store the connection string ------------------------------------------------
Write-Step 4 "Saving the connection string to $EnvVarName"
[Environment]::SetEnvironmentVariable($EnvVarName, $connectionString, 'User')
$connectionString = $null
Write-Ok "$EnvVarName saved for your Windows account (the password is not shown)"

Write-Host "`nDone. Close the R&D Studio and open it again - it reads $EnvVarName at start-up." -ForegroundColor Green
Write-Host "The first Request AI Translator saves every result; the next one loads them from the database.`n"
