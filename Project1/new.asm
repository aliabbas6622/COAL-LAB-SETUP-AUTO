INCLUDE Irvine32.inc

.data
attendance BYTE 0           ; total students marked present
increment  BYTE 1

.code
main PROC
    mov cl, 5              ; 5 students to mark present
    mov al, 0

L1:
    add al, increment      ; 1. Add first
    dec cl                 ; 2. Decrement counter
    jnz L1                 ; 3. Continue while CL != 0

Done:
    mov attendance, al     ; Stores 5 in attendance
    call DumpRegs
    exit
main ENDP
END main