<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>Pengin1011.Modules.__MODULE__</RootNamespace>
    <AssemblyName>__MODULE__</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <ModuleOutputDir Condition="'$(ModuleOutputDir)' == ''">$(MSBuildThisFileDirectory)..\..\bin\$(Configuration)\$(TargetFramework)\module\</ModuleOutputDir>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\Pengin1011.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.12" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.12">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>

  <Target Name="CopyToModuleDir" AfterTargets="Build">
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(ModuleOutputDir)" SkipUnchangedFiles="true" />
    <Copy SourceFiles="$(TargetDir)$(TargetName).pdb" DestinationFolder="$(ModuleOutputDir)" SkipUnchangedFiles="true" Condition="Exists('$(TargetDir)$(TargetName).pdb')" />
  </Target>

</Project>
