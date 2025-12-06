# ?? QUICK TEST GUIDE - AUDIT ENGINE BUG FIXES

## M?c ?ích
Ki?m tra nhanh các fix cho 4 bug nghiêm tr?ng trong AuditEngine.

---

## ? TEST NHANH (5 PHÚT)

### B??c 1: Ki?m Tra Direction Vectors (BUG #1)

**Command:**
```
DTS_TEST_AUDIT_FIX
```

**Input:**
- Load Pattern: `WYP` (ho?c b?t k? t?i gió/??ng ??t)

**K? v?ng:**
```
? Direction Components:
  - Sum |DirectionY|: > 0.01 (KHÔNG ph?i 0!)
  
? Dominant Direction: Y (ho?c X, Z tùy case)

? Found XX Lateral Wall Loads (KHÔNG ph?i 0!)
```

**? Fail n?u:**
- T?t c? Direction Components = 0
- "No Lateral Wall Loads found"

---

### B??c 2: Ki?m Tra Sai S? (BUG #2)

**Command:**
```
DTS_AUDIT_SAP2000
```

**Input:**
- Ch?n pattern: `DLW_AG` (ho?c b?t k? t?i có nhi?u ph?n t?)
- Unit: Ton
- Language: English

**So sánh v?i SAP2000:**
1. M? SAP2000 ? Run Analysis
2. Display ? Show Tables ? Analysis Results ? Base Reactions
3. L?c theo Load Case = DLW_AG
4. Xem c?t FZ (ho?c FX/FY n?u là t?i ngang)

**K? v?ng:**
- Sai s? < 5%: ? OK
- Sai s? 5-10%: ?? Ki?m tra l?i
- Sai s? > 10%: ? FAIL

---

### B??c 3: Ki?m Tra T?i Hình Thang (BUG #2B)

**Command:**
```
DTS_TEST_FRAME_LOAD
```

**Input:**
- Frame Name: (Ch?n 1 d?m có t?i hình thang, VD: B15)
- Load Pattern: DL

**Ki?m tra trong SAP2000:**
1. Assign ? Frame Loads ? Distributed
2. Xem Load có ph?i Trapezoidal không (FOverLA ? FOverLB)

**K? v?ng:**
- Value hi?n th? = (FOverLA + FOverLB) / 2 ?
- Không ch? l?y 1 ??u ?

---

## ?? TEST ??Y ?? (15 PHÚT)

### Test Matrix

| Load Case | Type | Expected Result | Pass/Fail |
|-----------|------|-----------------|-----------|
| DL | Gravity | Match SAP ± 1% | ? |
| LL | Gravity | Match SAP ± 1% | ? |
| WXP | Lateral X+ | DirectionX > 0 | ? |
| WXN | Lateral X- | DirectionX < 0 | ? |
| WYP | Lateral Y+ | DirectionY > 0 | ? |
| WYN | Lateral Y- | DirectionY < 0 | ? |
| EQX | Seismic X | Dominant = X | ? |
| EQY | Seismic Y | Dominant = Y | ? |

### Checklist

- [ ] T?t c? Load Case Gravity: Sai s? < 2%
- [ ] T?t c? Load Case Lateral: DirectionX/Y ? 0
- [ ] Base Reaction direction ?úng (Z cho gravity, X/Y cho lateral)
- [ ] Không còn double-counting (check s? l??ng loads h?p lý)
- [ ] T?i hình thang tính ?úng trung bình

---

## ?? TROUBLESHOOTING

### V?n ??: V?n báo 0.00 Ton cho t?i gió

**Nguyên nhân có th?:**
1. SAP model ch?a Run Analysis ? Ch?y Analysis tr??c
2. Load Pattern tên sai ? Check l?i tên chính xác trong SAP
3. UnitManager sai ? Ch?y `DTS_TEST_SAP` ?? check

**Gi?i pháp:**
```
DTS_TEST_SAP
```
- Verify unit: kN_mm_C ?
- Verify connection: OK ?

---

### V?n ??: Sai s? v?n > 10%

**Nguyên nhân có th?:**
1. Model có constraint/spring không ?úng
2. Load b? gán nh?m CoordSys (Local vs Global)
3. Có t?i trùng (assigned nhi?u l?n)

**Gi?i pháp:**
```
DTS_TEST_RAW_TABLE
```
- Ch?n b?ng: Frame Loads - Distributed
- Ki?m tra có dòng trùng không

---

### V?n ??: Build l?i

**Gi?i pháp:**
1. Clean Solution
2. Rebuild All
3. Check .NET Framework 4.8 installed

---

## ?? K?T QU? M?U (PASS)

```
=== TEST ULTIMATE: AUDIT FIX #1 + #2 ===

[1] Testing SapDatabaseReader...
    Total Loads Read: 1250
    Base Reaction: 201.34 kN ?
    Direction Components:
      - Sum |DirectionX|: 0.00
      - Sum |DirectionY|: 195.23 ?
      - Sum |DirectionZ|: 6.11
      - Dominant Direction: Y ?

[2] Checking for Trapezoidal Loads...
    Found 45 Frame Distributed Loads
    Sample 1: B15 = 7.50 kN/m (Dir: Gravity) ?

[3] Checking Lateral Loads on Walls...
    Found 348 Lateral Wall Loads: ?
      - Area245: Value=0.82, DirY=0.82
      - Area246: Value=0.74, DirY=0.74
      ...

[4] Testing AuditEngine...
    Stories Processed: 8
    Total Calculated: 201.15 kN
    SAP Base Reaction: 201.34 kN
    Difference: 0.09% ?

=== K?T LU?N ===
? FIX #1 OK: Direction Components ?ã ???c resolve
? FIX #2 OK: Sai s? < 10%
```

---

## ? ACCEPTANCE CRITERIA

Fix ???c coi là **PASS** khi:

1. ? T?i gió/??ng ??t report ? 0.00 Ton
2. ? Sai s? < 5% cho ít nh?t 3 Load Cases khác nhau
3. ? Direction Components (X/Y/Z) ???c resolve ?úng
4. ? T?i hình thang tính trung bình (không ch? 1 ??u)
5. ? Build thành công không l?i

---

## ?? BÁO CÁO K?T QU?

Sau khi test xong, ?i?n vào:

**Tester:** ___________________  
**Date:** ___________________  
**Overall Result:** ? PASS / ? FAIL  

**Issues Found:**
1. ___________________
2. ___________________
3. ___________________

**Signature:** ___________________

---

**L?u file này sau khi ?i?n và attach vào email/ticket support n?u c?n.**
