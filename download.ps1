# Set up target folders
$base = "wwwroot/lib"
$dtFolder = "$base/datatables"
$pluginFolder = "$base/datatables-plugins"

# Ensure folders exist
New-Item -ItemType Directory -Force -Path $dtFolder
New-Item -ItemType Directory -Force -Path $pluginFolder

# DataTables core
Invoke-WebRequest -Uri "https://cdn.datatables.net/1.13.8/js/jquery.dataTables.min.js" -OutFile "$dtFolder/jquery.dataTables.min.js"
Invoke-WebRequest -Uri "https://cdn.datatables.net/1.13.8/css/jquery.dataTables.min.css" -OutFile "$dtFolder/jquery.dataTables.min.css"

# RowGroup extension
Invoke-WebRequest -Uri "https://cdn.datatables.net/rowgroup/1.4.1/js/dataTables.rowGroup.min.js" -OutFile "$pluginFolder/dataTables.rowGroup.min.js"
Invoke-WebRequest -Uri "https://cdn.datatables.net/rowgroup/1.4.1/css/rowGroup.dataTables.min.css" -OutFile "$pluginFolder/rowGroup.dataTables.min.css"

Write-Host "DataTables and RowGroup have been downloaded!"