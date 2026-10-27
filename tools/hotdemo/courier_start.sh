set -u
C="F:/Courier/src/Courier.Cli/bin/Release/net10.0/courier.exe"
ls -l "$C" 2>/dev/null || find /f/Courier -name "courier.exe" -not -path "*/obj/*" 2>/dev/null | head -5
echo "=== courier.exe version amortized, 20 runs ==="
if [ -f "$C" ]; then S=$(date +%s%N); for i in $(seq 1 20); do "$C" version > /dev/null 2>&1; done; E=$(date +%s%N); echo "20 runs = $(( (E-S)/1000000 )) ms, $(( (E-S)/20000000 )) ms each"; fi