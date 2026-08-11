# PowerShell Script Launcher - Modern GUI Version
# Run this file to get a nice GUI menu for running Python scripts

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# Create main form
$form = New-Object System.Windows.Forms.Form
$form.Text = "Steam Plugin Manager - Script Launcher"
$form.Size = New-Object System.Drawing.Size(700, 600)
$form.StartPosition = "CenterScreen"
$form.BackColor = "#1e1e1e"
$form.ForeColor = "#00ff00"
$form.Font = New-Object System.Drawing.Font("Courier New", 10)

# Title label
$titleLabel = New-Object System.Windows.Forms.Label
$titleLabel.Text = "🐍 Python Script Launcher"
$titleLabel.Location = New-Object System.Drawing.Point(20, 20)
$titleLabel.Size = New-Object System.Drawing.Size(650, 30)
$titleLabel.Font = New-Object System.Drawing.Font("Courier New", 14, "Bold")
$titleLabel.ForeColor = "#00ff00"
$form.Controls.Add($titleLabel)

# Category selection
$categoryLabel = New-Object System.Windows.Forms.Label
$categoryLabel.Text = "Select Script Category:"
$categoryLabel.Location = New-Object System.Drawing.Point(20, 60)
$categoryLabel.Size = New-Object System.Drawing.Size(650, 20)
$form.Controls.Add($categoryLabel)

# Category buttons
$debugBtn = New-Object System.Windows.Forms.Button
$debugBtn.Text = "🐛 Debug Scripts"
$debugBtn.Location = New-Object System.Drawing.Point(20, 90)
$debugBtn.Size = New-Object System.Drawing.Size(150, 40)
$debugBtn.BackColor = "#2d2d2d"
$debugBtn.ForeColor = "#00ff00"
$form.Controls.Add($debugBtn)

$databaseBtn = New-Object System.Windows.Forms.Button
$databaseBtn.Text = "🗄️ Database Scripts"
$databaseBtn.Location = New-Object System.Drawing.Point(180, 90)
$databaseBtn.Size = New-Object System.Drawing.Size(150, 40)
$databaseBtn.BackColor = "#2d2d2d"
$databaseBtn.ForeColor = "#00ff00"
$form.Controls.Add($databaseBtn)

$testingBtn = New-Object System.Windows.Forms.Button
$testingBtn.Text = "✅ Testing Scripts"
$testingBtn.Location = New-Object System.Drawing.Point(340, 90)
$testingBtn.Size = New-Object System.Drawing.Size(150, 40)
$testingBtn.BackColor = "#2d2d2d"
$testingBtn.ForeColor = "#00ff00"
$form.Controls.Add($testingBtn)

$utilityBtn = New-Object System.Windows.Forms.Button
$utilityBtn.Text = "🔧 Utility Scripts"
$utilityBtn.Location = New-Object System.Drawing.Point(500, 90)
$utilityBtn.Size = New-Object System.Drawing.Size(150, 40)
$utilityBtn.BackColor = "#2d2d2d"
$utilityBtn.ForeColor = "#00ff00"
$form.Controls.Add($utilityBtn)

# Script selection label
$scriptLabel = New-Object System.Windows.Forms.Label
$scriptLabel.Text = "Select Script:"
$scriptLabel.Location = New-Object System.Drawing.Point(20, 140)
$scriptLabel.Size = New-Object System.Drawing.Size(650, 20)
$form.Controls.Add($scriptLabel)

# Script listbox
$listBox = New-Object System.Windows.Forms.ListBox
$listBox.Location = New-Object System.Drawing.Point(20, 165)
$listBox.Size = New-Object System.Drawing.Size(650, 300)
$listBox.BackColor = "#2d2d2d"
$listBox.ForeColor = "#00ff00"
$form.Controls.Add($listBox)

# Output text area
$outputLabel = New-Object System.Windows.Forms.Label
$outputLabel.Text = "Output:"
$outputLabel.Location = New-Object System.Drawing.Point(20, 470)
$outputLabel.Size = New-Object System.Drawing.Size(650, 20)
$form.Controls.Add($outputLabel)

# Run button
$runBtn = New-Object System.Windows.Forms.Button
$runBtn.Text = "▶ Run Selected Script"
$runBtn.Location = New-Object System.Drawing.Point(580, 495)
$runBtn.Size = New-Object System.Drawing.Size(90, 30)
$runBtn.BackColor = "#00aa00"
$runBtn.ForeColor = "#000000"
$runBtn.Font = New-Object System.Drawing.Font("Courier New", 9, "Bold")
$form.Controls.Add($runBtn)

# Exit button
$exitBtn = New-Object System.Windows.Forms.Button
$exitBtn.Text = "❌ Exit"
$exitBtn.Location = New-Object System.Drawing.Point(20, 495)
$exitBtn.Size = New-Object System.Drawing.Size(90, 30)
$exitBtn.BackColor = "#aa0000"
$exitBtn.ForeColor = "#ffffff"
$form.Controls.Add($exitBtn)

# Functions
function Show-DebugScripts {
    $listBox.Items.Clear()
    $listBox.Items.Add("debug_supabase.py - Debug Supabase RLS & Connectivity")
    $listBox.Items.Add("debug_cards.py - Debug Hz Manifest Card Display")
    $listBox.Items.Add("extended_debug.py - Extended Debugging with Logging")
    $listBox.Items.Add("quick_debug.py - Quick Troubleshooting")
    $listBox.Items.Add("quick_test.py - Quick Functionality Test")
    $script:currentCategory = "Debug"
}

function Show-DatabaseScripts {
    $listBox.Items.Clear()
    $listBox.Items.Add("populate_hzmanifest.py - Populate Hz Manifest (Main)")
    $listBox.Items.Add("populate_hzmanifest_v2.py - Populate Hz Manifest (V2)")
    $listBox.Items.Add("update_steam_names.py - Update Game Names")
    $listBox.Items.Add("update_steam_metadata.py - Update Game Metadata")
    $listBox.Items.Add("update_via_upsert.py - Update via UPSERT")
    $listBox.Items.Add("create_new_table_test.py - Test Table Creation")
    $script:currentCategory = "Database"
}

function Show-TestingScripts {
    $listBox.Items.Clear()
    $listBox.Items.Add("verify_hz_manifest.py - Hz Manifest Verification")
    $listBox.Items.Add("verify_hz_fix.py - Hz Fix Verification")
    $listBox.Items.Add("verify_metadata_update.py - Metadata Update Verify")
    $listBox.Items.Add("verify_new_features.py - New Features Verify")
    $listBox.Items.Add("verify_table.py - Table Verify")
    $listBox.Items.Add("verify_updates.py - Updates Verify")
    $listBox.Items.Add("final_hzmanifest_test.py - Final Hz Test")
    $listBox.Items.Add("final_test_connection.py - Connection Test")
    $listBox.Items.Add("final_update_game_names.py - Update Names Test")
    $listBox.Items.Add("test_appid_steam.py - Steam AppID Integration")
    $listBox.Items.Add("test_cards_binding.py - Cards Binding Test")
    $listBox.Items.Add("test_correct_table.py - Table Correctness")
    $script:currentCategory = "Testing"
}

function Show-UtilityScripts {
    $listBox.Items.Clear()
    $listBox.Items.Add("check_jwt.py - JWT Token Validation")
    $listBox.Items.Add("check_steam_appids.py - Steam AppID Validation")
    $listBox.Items.Add("check_name_difference.py - Compare Names")
    $listBox.Items.Add("check_one_record.py - Inspect Single Record")
    $listBox.Items.Add("list_rpc_functions.py - List RPC Functions")
    $listBox.Items.Add("try_rpc_functions.py - Test RPC Functions")
    $listBox.Items.Add("build_fix_report.py - Build Fix Report")
    $listBox.Items.Add("status_report.py - Status Report")
    $listBox.Items.Add("run_and_log.py - Run with Logging")
    $listBox.Items.Add("thumbnail_fix_verification.py - Thumbnail Verification")
    $script:currentCategory = "Utilities"
}

# Event handlers
$debugBtn.Add_Click({ Show-DebugScripts })
$databaseBtn.Add_Click({ Show-DatabaseScripts })
$testingBtn.Add_Click({ Show-TestingScripts })
$utilityBtn.Add_Click({ Show-UtilityScripts })

$runBtn.Add_Click({
    if ($listBox.SelectedIndex -eq -1) {
        [System.Windows.Forms.MessageBox]::Show("Please select a script first!", "Warning", "OK", "Warning")
        return
    }
    
    $selectedItem = $listBox.SelectedItem.ToString()
    $scriptName = $selectedItem.Split(" ")[0]
    $scriptPath = "Scripts\$script:currentCategory\$scriptName"
    
    # Run script
    $form.Enabled = $false
    Write-Host "Running: $scriptPath"
    & python "$scriptPath"
    $form.Enabled = $true
    
    [System.Windows.Forms.MessageBox]::Show("Script execution completed!", "Info", "OK", "Information")
})

$exitBtn.Add_Click({ $form.Close() })

# Show default category
Show-DebugScripts

# Show form
$form.ShowDialog() | Out-Null
