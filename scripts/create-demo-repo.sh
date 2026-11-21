#!/usr/bin/env bash
# Creates a small throwaway repository with a "feature" branch of several commits,
# handy for trying ReviewTool out. Usage: scripts/create-demo-repo.sh [target-dir]
set -euo pipefail
DIR="${1:-${TMPDIR:-/tmp}/reviewtool-demo}"
rm -rf "$DIR"
mkdir -p "$DIR"
cd "$DIR"
git init -q -b main
git config user.name "Demo Author"
git config user.email "demo@example.com"
c() { GIT_AUTHOR_DATE="$1" GIT_COMMITTER_DATE="$1" git commit -q -m "$2" ${3:+-m "$3"}; }

mkdir -p src docs
cat > src/calculator.cs <<'CS'
namespace Demo;

public static class Calculator
{
    public static int Add(int a, int b) => a + b;

    public static int Subtract(int a, int b) => a - b;
}
CS
cat > README.md <<'MD'
# Demo project

A tiny project used to try out ReviewTool.
MD
cat > docs/notes.txt <<'TXT'
Some notes that will be renamed later.
TXT
git add -A && c "2024-01-01T10:00:00Z" "Initial commit"

git checkout -q -b feature/math
cat > src/calculator.cs <<'CS'
namespace Demo;

public static class Calculator
{
    public static int Add(int a, int b) => a + b;

    public static int Subtract(int a, int b) => a - b;

    public static int Multiply(int a, int b) => a * b;

    public static int Divide(int a, int b)
    {
        return a / b;
    }
}
CS
git add -A && c "2024-01-02T10:00:00Z" "Add multiply and divide" "Divide does not guard against zero yet."

cat > src/CalculatorTests.cs <<'CS'
using Xunit;

namespace Demo.Tests;

public class CalculatorTests
{
    [Fact]
    public void Adds() => Assert.Equal(3, Calculator.Add(1, 2));

    [Fact]
    public void Multiplies() => Assert.Equal(6, Calculator.Multiply(2, 3));
}
CS
git add -A && c "2024-01-03T10:00:00Z" "Add calculator tests"

python3 - <<'PY'
import re
p = "src/calculator.cs"
s = open(p).read()
s = s.replace("""    public static int Divide(int a, int b)
    {
        return a / b;
    }""", """    public static int Divide(int a, int b)
    {
        if (b == 0) throw new System.DivideByZeroException("b must not be zero");
        return a / b;
    }

    public static double Average(params int[] values)
    {
        var sum = 0;
        foreach (var v in values) sum += v;
        return (double)sum / values.Length;
    }""")
open(p, "w").write(s)
PY
git add -A && c "2024-01-04T10:00:00Z" "Guard divide by zero, add Average"

git mv docs/notes.txt docs/design-notes.txt
printf 'Some notes that will be renamed later.\nNow with a second line.\n' > docs/design-notes.txt
cat >> README.md <<'MD'

## Usage

```csharp
var total = Calculator.Add(1, 2);
```
MD
printf '\x89PNG\r\n\x1a\n\x00\x00\x00binary' > docs/logo.png
git add -A && c "2024-01-05T10:00:00Z" "Docs: rename notes, document usage, add logo"

git checkout -q main
echo "Main moved on after the branch was cut." > CHANGELOG.md
git add -A && c "2024-01-06T10:00:00Z" "Add changelog on main"
git checkout -q feature/math

echo "Demo repository created at: $DIR"
echo "Review it with base 'main' and head 'feature/math'."
