// xUnit's attributes and Assert live in the Xunit namespace, which is not part of
// the .NET ImplicitUsings set. Declared once here so every test file in this
// project can use Fact/Theory/InlineData/Assert without a per-file using.
global using Xunit;
