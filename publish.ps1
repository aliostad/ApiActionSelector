param(
	$nugetApiKey = "$env:NUGET_API_KEY"
)

function require-param { 
    param($value, $paramName)
    
    if($value -eq $null) { 
        write-error "The parameter -$paramName is required."
    }
}

require-param $nugetApiKey -paramName "nugetApiKey"

#safely find the solutionDir
$ps1Dir = (Split-Path -parent $MyInvocation.MyCommand.Definition)
$solutionDir = $ps1Dir

$packages = dir "$solutionDir\artifacts\packages\*.nupkg"

foreach($package in $packages) { 
    #$package is type of System.IO.FileInfo
    & ".\.nuget\Nuget.exe" push $package.FullName $nugetApiKey
}